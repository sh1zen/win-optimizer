using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinOptimizer.Core.Features.Benchmark;

namespace WinOptimizer.Infrastructure.Features.Benchmark;

/// <summary>Measures an FP32 compute shader on the selected hardware adapter using GPU timestamps.</summary>
internal static class WindowsGpuBenchmark
{
    private const uint ThreadsPerGroup = 64;
    private const uint Groups = 512;
    private const int Iterations = 256;
    private const uint OpsPerIteration = 32; // Four float4 multiply-adds, each counting as two operations per lane.
    private const int DispatchesPerSample = 4;
    private const int MeasurementSamples = 8;
    private const uint TimestampQuery = 2;
    private const uint TimestampDisjointQuery = 3;
    private static readonly TimeSpan QueryDeadline = TimeSpan.FromSeconds(5);

    // Windows SDK d3d11.h and d3dcommon.h vtable slots, including inherited methods.
    private const int CreateBufferSlot = 3, CreateUavSlot = 8, CreateComputeShaderSlot = 18, CreateQuerySlot = 24;
    private const int BeginSlot = 27, EndSlot = 28, GetDataSlot = 29, DispatchSlot = 41;
    private const int SetUavSlot = 68, SetComputeShaderSlot = 69, FlushSlot = 111;
    private const int BlobPointerSlot = 3, BlobSizeSlot = 4;

    // Each thread writes a value derived from all four independent accumulation chains.
    private static readonly string Shader = $$"""
        RWStructuredBuffer<float4> output : register(u0);

        [numthreads({{ThreadsPerGroup}}, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            float4 seed = float(id.x & 1023u) * 0.0002 + float4(0.1, 0.2, 0.3, 0.4);
            float4 a = seed;
            float4 b = seed.yzwx + 0.07;
            float4 c = seed.zwxy + 0.13;
            float4 d = seed.wxyz + 0.17;
            [loop]
            for (uint i = 0; i < {{Iterations}}; ++i)
            {
                a = mad(a, 0.99991, 0.00001);
                b = mad(b, 0.99989, 0.00002);
                c = mad(c, 0.99987, 0.00003);
                d = mad(d, 0.99983, 0.00004);
            }
            output[id.x] = a + b + c + d;
        }
        """;

    internal static async Task<double> MeasureAsync(ulong? adapterId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (adapterId is null) throw new BenchmarkException(BenchmarkFailure.GraphicsUnavailable);
        try
        {
            foreach (var (info, adapter) in WindowsBenchmarkHardware.EnumerateGraphics())
            {
                token.ThrowIfCancellationRequested();
                if (info.Id == adapterId)
                    return await MeasureAdapterAsync(adapter, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is COMException or TimeoutException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new BenchmarkException(BenchmarkFailure.GraphicsUnavailable, error);
        }
        throw new BenchmarkException(BenchmarkFailure.GraphicsUnavailable);
    }

    private static async Task<double> MeasureAdapterAsync(GraphicsComObject adapter, CancellationToken token)
    {
        const uint DriverTypeUnknown = 0, SdkVersion = 7, FeatureLevel11 = 0xB000;
        var createStatus = D3D11CreateDevice(adapter.DangerousGetHandle(), DriverTypeUnknown, IntPtr.Zero, 0,
            FeatureLevel11, 1, SdkVersion, out var devicePointer, IntPtr.Zero, out var contextPointer);
        using var device = new GraphicsComObject(devicePointer);
        using var context = new GraphicsComObject(contextPointer);
        Check(createStatus, "D3D11CreateDevice (feature level 11_0)");
        using var shader = CreateShader(device);
        using var buffer = CreateOutputBuffer(device);
        using var output = CreateOutputView(device, buffer);
        using var start = CreateTimestampQuery(device, TimestampQuery);
        using var end = CreateTimestampQuery(device, TimestampQuery);
        using var disjoint = CreateTimestampQuery(device, TimestampDisjointQuery);
        token.ThrowIfCancellationRequested();

        var outputPointer = output.DangerousGetHandle();
        context.Method<SetUnorderedAccessViews>(SetUavSlot)(contextPointer, 0, 1, in outputPointer, IntPtr.Zero);
        context.Method<SetComputeShader>(SetComputeShaderSlot)(contextPointer, shader.DangerousGetHandle(), IntPtr.Zero, 0);
        var beginQuery = context.Method<QueryCommand>(BeginSlot);
        var endQuery = context.Method<QueryCommand>(EndSlot);
        var dispatch = context.Method<Dispatch>(DispatchSlot);
        var flush = context.Method<ContextCommand>(FlushSlot);
        var getData = context.Method<GetData>(GetDataSlot);
        var disjointSize = Marshal.SizeOf<DisjointData>();
        var queryData = Marshal.AllocHGlobal(disjointSize);
        try
        {
            var deadline = Stopwatch.StartNew();
            var validSamples = 0;
            double seconds = 0;
            // The first sample warms the shader and GPU before collecting measurements.
            for (int sample = -1; sample < MeasurementSamples; sample++)
            {
                token.ThrowIfCancellationRequested();
                beginQuery(contextPointer, disjoint.DangerousGetHandle());
                endQuery(contextPointer, start.DangerousGetHandle());
                for (var i = 0; i < DispatchesPerSample; i++)
                    dispatch(contextPointer, Groups, 1, 1);
                endQuery(contextPointer, end.DangerousGetHandle());
                endQuery(contextPointer, disjoint.DangerousGetHandle());
                flush(contextPointer);

                await ReadDataAsync(disjoint, (uint)disjointSize).ConfigureAwait(false);
                var timing = Marshal.PtrToStructure<DisjointData>(queryData);
                if (sample < 0 || timing.Disjoint != 0 || timing.Frequency == 0) continue;
                await ReadDataAsync(start, sizeof(ulong)).ConfigureAwait(false);
                var startTick = unchecked((ulong)Marshal.ReadInt64(queryData));
                await ReadDataAsync(end, sizeof(ulong)).ConfigureAwait(false);
                var endTick = unchecked((ulong)Marshal.ReadInt64(queryData));
                if (endTick <= startTick) continue;
                validSamples++;
                seconds += (double)(endTick - startTick) / timing.Frequency;
            }
            if (validSamples == 0) throw new COMException("The GPU did not return any valid timestamp intervals.");
            return validSamples * (double)DispatchesPerSample * Groups * ThreadsPerGroup * Iterations * OpsPerIteration / seconds;

            async Task ReadDataAsync(GraphicsComObject query, uint size)
            {
                const uint DoNotFlush = 1;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (deadline.Elapsed >= QueryDeadline)
                        throw new TimeoutException("Timed out waiting for Direct3D GPU timestamps.");
                    var status = getData(contextPointer, query.DangerousGetHandle(), queryData, size, DoNotFlush);
                    Check(status, "ID3D11DeviceContext.GetData");
                    if (status == 0) return;
                    await Task.Delay(1, token).ConfigureAwait(false);
                }
            }
        }
        finally { Marshal.FreeHGlobal(queryData); }
    }

    private static GraphicsComObject CreateShader(GraphicsComObject device)
    {
        const uint Optimize = 1 << 15;
        var source = Encoding.ASCII.GetBytes(Shader);
        var status = D3DCompile(source, (nuint)source.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            "main", "cs_5_0", Optimize, 0, out var codePointer, out var errorPointer);
        using var code = new GraphicsComObject(codePointer);
        using var errors = new GraphicsComObject(errorPointer);
        if (status < 0 && !errors.IsInvalid)
        {
            var message = Marshal.PtrToStringAnsi(errors.Method<GetBlobPointer>(BlobPointerSlot)(errorPointer));
            throw new COMException($"D3DCompile failed: {message}", status);
        }
        Check(status, "D3DCompile");
        var shader = code.Method<GetBlobPointer>(BlobPointerSlot)(codePointer);
        var size = code.Method<GetBlobSize>(BlobSizeSlot)(codePointer);
        status = device.Method<CreateComputeShader>(CreateComputeShaderSlot)(device.DangerousGetHandle(),
            shader, size, IntPtr.Zero, out var shaderPointer);
        return TakeResult(status, shaderPointer, "ID3D11Device.CreateComputeShader");
    }

    private static GraphicsComObject CreateOutputBuffer(GraphicsComObject device)
    {
        const uint BindUnorderedAccess = 0x80, StructuredBuffer = 0x40, Stride = 4 * sizeof(float);
        var description = new BufferDescription(Groups * ThreadsPerGroup * Stride, 0, BindUnorderedAccess, 0, StructuredBuffer, Stride);
        var status = device.Method<CreateBuffer>(CreateBufferSlot)(device.DangerousGetHandle(), in description,
            IntPtr.Zero, out var pointer);
        return TakeResult(status, pointer, "ID3D11Device.CreateBuffer");
    }

    private static GraphicsComObject CreateOutputView(GraphicsComObject device, GraphicsComObject buffer)
    {
        const uint FormatUnknown = 0, BufferView = 1;
        var description = new UnorderedAccessViewDescription(FormatUnknown, BufferView, 0, Groups * ThreadsPerGroup, 0);
        var status = device.Method<CreateUnorderedAccessView>(CreateUavSlot)(device.DangerousGetHandle(),
            buffer.DangerousGetHandle(), in description, out var pointer);
        return TakeResult(status, pointer, "ID3D11Device.CreateUnorderedAccessView");
    }

    private static GraphicsComObject CreateTimestampQuery(GraphicsComObject device, uint queryType)
    {
        var description = new QueryDescription(queryType, 0);
        var status = device.Method<CreateQuery>(CreateQuerySlot)(device.DangerousGetHandle(), in description, out var pointer);
        return TakeResult(status, pointer, "ID3D11Device.CreateQuery");
    }

    private static GraphicsComObject TakeResult(int status, IntPtr pointer, string operation)
    {
        var result = new GraphicsComObject(pointer);
        if (status >= 0) return result;
        result.Dispose();
        throw new COMException($"{operation} failed (0x{status:X8}).", status);
    }

    private static void Check(int status, string operation)
    {
        if (status < 0) throw new COMException($"{operation} failed (0x{status:X8}).", status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct BufferDescription(uint ByteWidth, uint Usage, uint BindFlags, uint CpuAccessFlags,
        uint MiscFlags, uint StructureByteStride);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct UnorderedAccessViewDescription(uint Format, uint ViewDimension, uint FirstElement,
        uint NumElements, uint Flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct QueryDescription(uint Query, uint MiscFlags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DisjointData
    {
        public readonly ulong Frequency;
        public readonly int Disjoint;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateBuffer(IntPtr device, in BufferDescription description, IntPtr initialData, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateUnorderedAccessView(IntPtr device, IntPtr buffer, in UnorderedAccessViewDescription description, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateComputeShader(IntPtr device, IntPtr code, nuint size, IntPtr classLinkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateQuery(IntPtr device, in QueryDescription description, out IntPtr query);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetUnorderedAccessViews(IntPtr context, uint startSlot, uint count, in IntPtr views, IntPtr initialCounts);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetComputeShader(IntPtr context, IntPtr shader, IntPtr classInstances, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ContextCommand(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void QueryCommand(IntPtr context, IntPtr query);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Dispatch(IntPtr context, uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetData(IntPtr context, IntPtr query, IntPtr data, uint size, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBlobPointer(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint GetBlobSize(IntPtr blob);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        in uint featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, IntPtr actualLevel, out IntPtr context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DCompile([In] byte[] source, nuint size, IntPtr sourceName, IntPtr defines, IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags, uint effectFlags, out IntPtr code, out IntPtr errors);
}
