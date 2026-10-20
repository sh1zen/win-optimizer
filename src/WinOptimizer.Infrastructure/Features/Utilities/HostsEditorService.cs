using WinOptimizer.Core.Features.Common.Constants;
using System.Security.Cryptography;
using System.Text;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Owns the fixed hosts path, exact-byte backups, encoding and atomic replacement.</summary>
public sealed class HostsEditorService : IHostsEditorService
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly string _backupFolder;
    private readonly IApplicationModeService _mode;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HostsEditorService(IApplicationModeService mode) : this(mode,
        Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts"),
        Path.Combine(AppDataPaths.Backups, "Hosts")) { }

    internal HostsEditorService(IApplicationModeService mode, string path, string backupFolder)
    {
        _mode = mode; _path = Path.GetFullPath(path); _backupFolder = Path.GetFullPath(backupFolder);
    }

    public Task<HostsDocument> ReadAsync() => Task.Run(() => ToDocument(ReadBytes()));

    public async Task<HostsSaveResult> SaveAsync(HostsDocument original, string content)
    {
        HostsSyntax.Validate(content);
        if (!string.Equals(Path.GetFullPath(original.FilePath), _path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The document does not belong to the system hosts file.");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = _mode.BeginSystemOperation();
            if (_mode.CurrentMode != WinOptimizerMode.Normal) throw new InvalidOperationException("Hosts edits require normal application mode.");
            return await Task.Run(() => Save(original, content)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private HostsSaveResult Save(HostsDocument original, string content)
    {
        var bytes = ReadBytes();
        if (Revision(bytes) != original.Revision)
            throw new UtilityOperationException("The hosts file changed outside this editor. Reload it before saving.");
        var (_, encoding, preamble) = Decode(bytes);
        var updated = preamble.Concat(encoding.GetBytes(content)).ToArray();
        if (updated.Length > MaximumBytes) throw new ArgumentException("The hosts file exceeds the 4 MB editor limit.");
        Directory.CreateDirectory(_backupFolder);
        var backup = Path.Combine(_backupFolder, $"hosts-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak");
        using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { output.Write(bytes); output.Flush(true); }
        if (Revision(File.ReadAllBytes(backup)) != original.Revision)
            throw new UtilityOperationException("The hosts backup could not be verified. The system file was not changed.");
        var temporary = Path.Combine(Path.GetDirectoryName(_path)!, $".hosts-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(updated); output.Flush(true); }
            if (Revision(ReadBytes()) != original.Revision)
                throw new UtilityOperationException("The hosts file changed while saving. Your edit was not applied.");
            // File.Replace keeps the destination's ACL; the original bytes are already backed up.
            File.Replace(temporary, _path, null);
            return new(ToDocument(updated), backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private byte[] ReadBytes()
    {
        var info = new FileInfo(_path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UtilityOperationException("The hosts file is a symbolic link or reparse point; direct editing is unavailable.");
        if (info.Length > MaximumBytes) throw new UtilityOperationException("The hosts file exceeds the 4 MB editor limit.");
        return File.ReadAllBytes(_path);
    }

    private HostsDocument ToDocument(byte[] bytes) => new(Decode(bytes).Content, Revision(bytes), _path);
    private static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Retains BOMs and decodes legacy Windows comments without replacement characters.</summary>
    internal static (string Content, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
    {
        Encoding encoding;
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = new UTF8Encoding(false, true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        else
        {
            encoding = new UTF8Encoding(false, true);
            try { return (encoding.GetString(bytes), encoding, []); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding((int)GetACP(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }
        return (encoding.GetString(bytes, offset, bytes.Length - offset), encoding, bytes[..offset]);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
