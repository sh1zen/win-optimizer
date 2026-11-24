using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Exchanges one settings request with a companion running in the desktop account.</summary>
public static class DesktopExtensionSettingsChannel
{
    internal const string WorkerArgument = "--settings-worker";
    private const int MaximumMessageBytes = 1024 * 1024;
    private const PipeAccessRights ClientAccess = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static string PipeName(Guid connection) => $"WinOptimizer.DesktopExtension.Settings.{connection:N}";

    private sealed record Response<T>(T? Settings, string? Error = null) where T : class;

    public static bool TryParseWorkerArguments(string[] args, out Guid connection, out string? startLanguage)
    {
        connection = default;
        startLanguage = args is [WorkerArgument, _, var language] ? language : null;
        return args is [WorkerArgument, _, ..] && args.Length <= 3 && Guid.TryParseExact(args[1], "N", out connection);
    }

    /// <summary>Reads the worker suffix from the unpackaged activation's original command line.</summary>
    public static bool TryParseRedirectedWorker(string commandLine, out Guid connection)
    {
        connection = default;
        var arguments = CommandLineToArgv(commandLine, out int count);
        if (arguments == 0) return false;
        try
        {
            if (count is not (3 or 4)) return false;
            var args = new string[count - 1];
            for (var index = 1; index < count; index++)
                args[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size)) ?? "";
            return TryParseWorkerArguments(args, out connection, out _);
        }
        finally { LocalFree(arguments); }
    }

    internal static async Task<TResponse> SendAsync<TRequest, TResponse>(DesktopExtensionProcess process, TRequest request,
        string? startLanguage = null) where TResponse : class
    {
        var connection = Guid.NewGuid();
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new InvalidOperationException("The current token does not identify a Windows account.");
        var desktopUser = DesktopExtensionProcess.DesktopUser();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!desktopUser.Equals(currentUser))
            security.AddAccessRule(new PipeAccessRule(desktopUser, ClientAccess, AccessControlType.Allow));

        using var pipe = NamedPipeServerStreamAcl.Create(PipeName(connection), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await Task.Run(() => process.OpenSettingsWorker(connection, startLanguage), timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            return await ExchangeAsync<TRequest, TResponse>(pipe, request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"{process.Name} did not respond to the settings request from the desktop. Try again.");
        }
    }

    /// <summary>Invokes the handler on the caller's context so a running companion can update its controls.</summary>
    internal static async Task ReceiveAsync<TRequest, TResponse>(Guid connection, Func<TRequest, Task<TResponse>> handle)
        where TResponse : class
    {
        DesktopExtensionProcess.RequireNonElevated();
        using var timeout = new CancellationTokenSource(Timeout);
        using var pipe = new NamedPipeClientStream(".", PipeName(connection), ClientAccess,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(timeout.Token);
        await ServeAsync(pipe, handle, timeout.Token);
    }

    internal static async Task<TResponse> ExchangeAsync<TRequest, TResponse>(Stream pipe, TRequest request, CancellationToken ct)
        where TResponse : class
    {
        await WriteAsync(pipe, request, ct).ConfigureAwait(false);
        var response = await ReadAsync<Response<TResponse>>(pipe, ct).ConfigureAwait(false);
        return response.Settings ?? throw new IOException(response.Error ?? "The desktop extension returned no settings.");
    }

    internal static async Task ServeAsync<TRequest, TResponse>(Stream pipe, Func<TRequest, Task<TResponse>> handle, CancellationToken ct)
        where TResponse : class
    {
        Response<TResponse> response;
        try
        {
            var request = await ReadAsync<TRequest>(pipe, ct);
            response = new(await handle(request));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or JsonException or SecurityException)
        {
            response = new(null, ex.Message);
        }

        await WriteAsync(pipe, response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid desktop extension settings message size.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Empty desktop extension settings message.");
    }

    private static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("The desktop extension settings message is too large.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CommandLineToArgv(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
