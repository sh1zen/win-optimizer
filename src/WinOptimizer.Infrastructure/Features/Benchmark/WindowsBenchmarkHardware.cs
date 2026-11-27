using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using WinOptimizer.Core.Features.Benchmark;

namespace WinOptimizer.Infrastructure.Features.Benchmark;

/// <summary>Reads the processor and display-adapter values exposed by the local platform.</summary>
internal static class WindowsBenchmarkHardware
{
    private const int ProcessorInformation = 11;
    // Windows SDK dxgi.h vtable slots, including the inherited IUnknown and DXGI methods.
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;
    private const uint SoftwareAdapter = 2;
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    internal static BenchmarkProcessor? ReadProcessor()
    {
        if (!X86Base.IsSupported || X86Base.CpuId(0, 0).Eax < 1) return null;
        var (signature, _, _, features) = X86Base.CpuId(1, 0);
        var family = (signature >> 8) & 0xF;
        var model = (signature >> 4) & 0xF;
        if (family is 6 or 15) model |= ((signature >> 16) & 0xF) << 4;
        if (family == 15) family += (signature >> 20) & 0xFF;

        var instructions = new List<string>();
        if ((features & (1 << 23)) != 0) instructions.Add("MMX");
        if (Sse.IsSupported) instructions.Add("SSE");
        if (Sse2.IsSupported) instructions.Add("SSE2");
        if (Sse3.IsSupported) instructions.Add("SSE3");
        if (Ssse3.IsSupported) instructions.Add("SSSE3");
        if (Sse41.IsSupported) instructions.Add("SSE4.1");
        if (Sse42.IsSupported) instructions.Add("SSE4.2");
        if (Avx.IsSupported) instructions.Add("AVX");
        if (Avx2.IsSupported) instructions.Add("AVX2");
        if (Fma.IsSupported) instructions.Add("FMA");
        if (Avx512F.IsSupported) instructions.Add("AVX-512F");
        return new(family, model, signature & 0xF, instructions);
    }

    /// <summary>Averages the current MHz values reported by Windows across logical processors and six samples.</summary>
    internal static async Task<double> SampleClockAsync(CancellationToken token)
    {
        const int sampleCount = 6;
        var count = GetActiveProcessorCount(ushort.MaxValue);
        if (count == 0) throw new BenchmarkException(BenchmarkFailure.ClockUnavailable);
        var readings = new ProcessorPowerInformation[checked((int)count)];
        var bytes = checked(count * (uint)Marshal.SizeOf<ProcessorPowerInformation>());
        long total = 0;
        int valid = 0;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(readings);
            var status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, readings, bytes);
            if (status != 0) throw new BenchmarkException(BenchmarkFailure.ClockUnavailable,
                new InvalidOperationException($"CallNtPowerInformation returned NTSTATUS 0x{status:X8}."));
            foreach (var reading in readings)
            {
                if (reading.CurrentMhz == 0) continue;
                total += reading.CurrentMhz;
                valid++;
            }
            if (sample < sampleCount - 1)
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
        return valid == 0 ? throw new BenchmarkException(BenchmarkFailure.ClockUnavailable) : (double)total / valid;
    }

    /// <summary>Reads dedicated video memory for each physical adapter using the pointer-sized DXGI field.</summary>
    internal static IReadOnlyList<BenchmarkGraphicsAdapter> ReadGraphics() =>
        EnumerateGraphics().Select(adapter => adapter.Info).ToArray();

    /// <summary>Yields each hardware adapter with a handle valid for the current iteration.</summary>
    internal static IEnumerable<(BenchmarkGraphicsAdapter Info, GraphicsComObject Handle)> EnumerateGraphics()
    {
        var status = CreateDXGIFactory1(in FactoryId, out var factoryPointer);
        using var factory = new GraphicsComObject(factoryPointer);
        if (status < 0) yield break;

        var enumerate = factory.Method<EnumAdapters1>(EnumAdapters1Slot);
        for (uint i = 0; enumerate(factoryPointer, i, out var adapterPointer) >= 0; i++)
        {
            using var adapter = new GraphicsComObject(adapterPointer);
            if (adapter.Method<GetDesc1>(GetDesc1Slot)(adapterPointer, out var description) >= 0 &&
                (description.Flags & SoftwareAdapter) == 0)
            {
                var id = ((ulong)(uint)description.LuidHighPart << 32) | description.LuidLowPart;
                yield return (new(description.Description.Trim(), (ulong)description.DedicatedVideoMemory, id), adapter);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr adapter, out AdapterDescription description);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetActiveProcessorCount(ushort group);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputLength,
        [Out] ProcessorPowerInformation[] outputBuffer, uint outputLength);
}
