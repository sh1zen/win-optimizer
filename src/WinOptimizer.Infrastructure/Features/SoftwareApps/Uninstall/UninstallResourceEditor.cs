using Microsoft.Win32;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

/// <summary>Revalidates exact evidence at each mutation and preserves changed, busy, linked or unselected resources.</summary>
internal sealed class UninstallResourceEditor(ICleanerRegistrySessions sessions, IUninstallScanFolders folders,
    ICleanerShortcutReader shortcuts)
{
    public void Verify(UninstallEvidence evidence)
    {
        if (evidence.Root != null)
        {
            if (evidence.File != null) { using var source = OpenBackupFile(evidence); }
            else
            {
                ValidateFilePath(evidence);
                var directory = new DirectoryInfo(evidence.Finding.Path);
                if (!directory.Exists || directory.LastWriteTimeUtc != evidence.Finding.ModifiedUtc)
                    throw new IOException("The directory changed after the scan; scan again.");
            }
            return;
        }
        using var account = OpenRegistrySession(evidence);
        if (evidence.Finding.Kind == UninstallArtifactKind.RegistryKey)
        {
            if (!account.Reader.KeyExists(evidence.Finding.Path, evidence.Finding.View32))
                throw new IOException("The selected registry key is no longer present.");
        }
        else
        {
            using var key = account.Editor.OpenKey(evidence.Finding.Path, evidence.Finding.View32, false);
            VerifyValue(evidence, key);
        }
    }

    public FileStream OpenBackupFile(UninstallEvidence evidence)
    {
        ValidateFilePath(evidence);
        return CleanerFileDeletion.OpenVerified(evidence.File ?? throw new IOException("File evidence is missing."), deleting: false);
    }

    public async Task DeleteAsync(UninstallEvidence evidence, string? backupHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (evidence.Root != null)
        {
            ValidateFilePath(evidence);
            if (evidence.File != null)
            {
                using var source = CleanerFileDeletion.OpenVerified(evidence.File, deleting: true);
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(source, ct).ConfigureAwait(false));
                if (hash != backupHash) throw new IOException("The selected file differs from its verified backup; it has been preserved.");
                ct.ThrowIfCancellationRequested();
                CleanerFileDeletion.MarkForDeletion(source.SafeFileHandle);
            }
            else Directory.Delete(evidence.Finding.Path, recursive: false);
            return;
        }
        using var account = OpenRegistrySession(evidence);
        if (evidence.Finding.Kind == UninstallArtifactKind.RegistryKey)
        {
            var (parent, name) = SplitRegistryPath(evidence.Finding.Path);
            using var key = account.Editor.OpenKey(parent, evidence.Finding.View32, true);
            key.DeleteEmptySubKey(name);
        }
        else
        {
            using var key = account.Editor.OpenKey(evidence.Finding.Path, evidence.Finding.View32, true);
            VerifyValue(evidence, key);
            key.DeleteValue(evidence.Finding.ValueName!);
        }
    }

    public async Task RestoreAsync(UninstallEvidence evidence, string? payload, string? expectedHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (evidence.Root != null)
        {
            ValidateFilePath(evidence);
            if (evidence.File == null) { Directory.CreateDirectory(evidence.Finding.Path); return; }
            if (File.Exists(evidence.Finding.Path))
            {
                using var current = new FileStream(evidence.Finding.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(current, ct).ConfigureAwait(false)) != expectedHash)
                    throw new IOException("A different file exists at the recovery destination; it has been preserved.");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(evidence.Finding.Path)!);
            ValidateFilePath(evidence);
            using var source = new FileStream(payload ?? throw new IOException("The backup payload is missing."), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = CleanerFileDeletion.CreateRestoreFile(evidence.Finding.Path);
            try
            {
                var restoredHash = await CleanerBackupService.CopyVerifiedAsync(source, destination, ct).ConfigureAwait(false);
                if (restoredHash != expectedHash) throw new IOException("The restored file failed integrity verification.");
                File.SetLastWriteTimeUtc(destination.SafeFileHandle, evidence.File.LastWriteUtc);
            }
            catch
            {
                CleanerFileDeletion.MarkForDeletion(destination.SafeFileHandle);
                throw;
            }
            return;
        }
        using var account = OpenRegistrySession(evidence);
        if (evidence.Finding.Kind == UninstallArtifactKind.RegistryKey)
        {
            if (account.Reader.KeyExists(evidence.Finding.Path, evidence.Finding.View32)) return;
            var (parent, name) = SplitRegistryPath(evidence.Finding.Path);
            using var key = account.Editor.OpenKey(parent, evidence.Finding.View32, true);
            using var restored = key.CreateSubKey(name);
        }
        else
        {
            using var key = account.Editor.OpenKey(evidence.Finding.Path, evidence.Finding.View32, true);
            var current = key.ReadValue(evidence.Finding.ValueName!);
            if (current != null)
            {
                if ((int)current.Kind != evidence.RegistryKind || UninstallResources.Serialize(current) != evidence.RegistryData)
                    throw new IOException("A different registry value exists; it has been preserved.");
                return;
            }
            key.SetValue(new(evidence.Finding.ValueName!, UninstallResources.Deserialize(evidence.RegistryKind,
                evidence.RegistryData ?? throw new IOException("Registry backup data is missing.")), (RegistryValueKind)evidence.RegistryKind));
        }
    }

    private void ValidateFilePath(UninstallEvidence evidence)
    {
        var root = evidence.Root ?? throw new IOException("The file's discovery boundary is missing.");
        UninstallResources.ValidateRoot(root, sessions, folders);
        if (root.SingleFile ? !root.Path.Equals(evidence.Finding.Path, StringComparison.OrdinalIgnoreCase)
            : !UninstallResources.Under(evidence.Finding.Path, root.Path))
            throw new IOException("The selected path is outside its product-specific directory.");
        CleanerPaths.ValidateDescendant(Path.GetDirectoryName(root.Path)!, evidence.Finding.Path);
        if (evidence.File != null && (!evidence.File.Path.Equals(evidence.Finding.Path, StringComparison.OrdinalIgnoreCase) ||
            !evidence.File.Root.Equals(Path.GetDirectoryName(root.Path), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The file evidence does not match its discovery boundary.");
        if (root.ShortcutTarget != null && File.Exists(evidence.Finding.Path))
        {
            using var account = sessions.Open(root.Sid);
            if (!CleanerAccountEnvironment.Expand(shortcuts.ReadTarget(evidence.Finding.Path), RegistryView.Registry64, account)
                .Equals(root.ShortcutTarget, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The shortcut target changed after discovery; it has been preserved.");
        }
    }

    private ICleanerRegistrySession OpenRegistrySession(UninstallEvidence evidence)
    {
        var account = sessions.Open(evidence.Sid);
        var prefix = evidence.Sid == null ? "HKEY_LOCAL_MACHINE" : account.UserPrefix;
        if (!evidence.Finding.Path.StartsWith(prefix + @"\Software\", StringComparison.OrdinalIgnoreCase) ||
            evidence.Finding.Path.IndexOfAny(['\0', '\r', '\n']) >= 0 || evidence.Finding.ValueName?.Contains('\0') == true)
        {
            account.Dispose();
            throw new IOException("The registry resource is outside its captured account and software branch.");
        }
        return account;
    }

    private static void VerifyValue(UninstallEvidence evidence, ICleanerRegistryKey key)
    {
        var current = key.ReadValue(evidence.Finding.ValueName ?? throw new IOException("The registry value name is missing."));
        if (current == null || (int)current.Kind != evidence.RegistryKind || UninstallResources.Serialize(current) != evidence.RegistryData)
            throw new IOException("The registry value changed after the scan; it has been preserved.");
    }

    private static (string Parent, string Name) SplitRegistryPath(string path)
    {
        var separator = path.LastIndexOf('\\');
        if (separator < 1 || separator == path.Length - 1) throw new IOException("The registry path is invalid.");
        return (path[..separator], path[(separator + 1)..]);
    }
}
