using Microsoft.Win32;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;

internal sealed record LeftoverCandidate(string Identity, LeftoverItem Item, IReadOnlyList<UninstallEvidence> Evidence)
{
    public string Fingerprint => string.Join("\n", Evidence.OrderBy(entry => entry.Finding.Path, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Finding.ValueName, StringComparer.OrdinalIgnoreCase).Select(entry =>
            $"{UninstallResources.PhysicalIdentity(entry)}|{entry.RegistryKind}|{entry.RegistryData}|{entry.File?.Length}|{entry.File?.LastWriteUtc.Ticks}|{entry.Finding.ModifiedUtc?.Ticks}"));
}
internal sealed record LeftoverInventory(IReadOnlyList<LeftoverCandidate> Candidates, IReadOnlyList<string> Notices);
internal interface ILeftoverSource { LeftoverInventory Read(CancellationToken ct); }

/// <summary>Finds selectable stale registrations, owned installation remnants and aged unreferenced repair-cache files.</summary>
internal sealed class WindowsLeftoverSource(IUninstallAccountSessions sessions, IUninstallScanFolders folders,
    IInstallerInventory installer, TimeProvider? clock = null) : ILeftoverSource
{
    public LeftoverInventory Read(CancellationToken ct)
    {
        var references = installer.Read(ct);
        List<string> notices = [.. references.Notices];
        List<LeftoverCandidate> candidates = [];
        List<LeftoverDirectory> directories = [];
        HashSet<string> activeDirectories = new(StringComparer.OrdinalIgnoreCase);
        var complete = references.Complete;
        bool directoriesComplete = true;
        bool pending = false;
        var accounts = new List<string?> { null };
        try { accounts.AddRange(sessions.GetAccounts().Select(account => account.Sid)); }
        catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add(ex.Message); complete = false; }
        foreach (var sid in accounts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var account = sessions.Open(sid);
                if (sid == null)
                {
                    pending = account.Reader.KeyExists(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Installer\InProgress", false) ||
                        account.Reader.KeyExists(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", false) ||
                        account.Reader.KeyExists(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", false) ||
                        account.Reader.GetValues(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", false, ct)
                            .Any(value => value.Name.Equals("PendingFileRenameOperations", StringComparison.OrdinalIgnoreCase));
                }
                foreach (var view32 in new[] { false, true })
                {
                    var branch = (sid == null ? "HKEY_LOCAL_MACHINE" : account.UserPrefix) + "\\" +
                        (sid != null && view32 ? @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" : UninstallResources.UninstallBranch);
                    foreach (var key in account.Reader.GetSubKeyNames(branch, view32))
                    {
                        ct.ThrowIfCancellationRequested();
                        var path = branch + "\\" + key;
                        try
                        {
                            var values = account.Reader.GetValues(path, view32, ct);
                            string Text(string name) => values.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Data as string ?? "";
                            string Expand(string value) => UninstallResources.ExpandMetadata(value, account, sid, view32);
                            // Hidden registrations can own repair caches too. Read references before filtering display entries.
                            foreach (var field in new[] { "UninstallString", "QuietUninstallString", "ModifyPath", "RepairPath", "DisplayIcon", "BundleCachePath", "InstallSource" })
                            {
                                if (values.Any(value => value.Name.Equals(field, StringComparison.OrdinalIgnoreCase) && value.Data is not string))
                                { complete = false; notices.Add($"{path}: {field} has an invalid registry value type."); continue; }
                                var raw = Text(field);
                                if (string.IsNullOrWhiteSpace(raw)) continue;
                                var expanded = Expand(raw);
                                var target = field == "DisplayIcon" ? CleanerRegistryScanner.ExtractIconPath(expanded) :
                                    field is "UninstallString" or "QuietUninstallString" or "ModifyPath" or "RepairPath" ? CleanerRegistryScanner.ExtractCommandPath(expanded) :
                                    UninstallResources.LocalPath(expanded);
                                if (target == null)
                                {
                                    // Relative launchers do not prove cache ownership; unresolved cache paths block cache deletion.
                                    if (field is "BundleCachePath" or "InstallSource" ||
                                        expanded.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ||
                                        expanded.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase))
                                    { complete = false; notices.Add($"{path}: the installer reference in {field} could not be resolved."); }
                                    continue;
                                }
                                target = CleanerFileDeletion.ResolveReferencePath(target);
                                if (field == "InstallSource") references.SourceDirectories.Add(target);
                                else if (field == "BundleCachePath") references.SourceDirectories.Add(Path.GetDirectoryName(target)!);
                                else references.Files.Add(target);
                            }
                            var rawLocation = Expand(Text("InstallLocation"));
                            var location = UninstallResources.LocalPath(rawLocation);
                            if (values.Any(value => value.Name.Equals("InstallLocation", StringComparison.OrdinalIgnoreCase) && value.Data is not string) ||
                                !string.IsNullOrWhiteSpace(rawLocation) && location == null)
                            {
                                directoriesComplete = false;
                                notices.Add($"{path}: installation-directory metadata could not be resolved; application folders were excluded.");
                            }
                            var icon = CleanerRegistryScanner.ExtractIconPath(Expand(Text("DisplayIcon")));
                            var command = CleanerRegistryScanner.ExtractCommandPath(Expand(Text("UninstallString")));
                            bool msi = values.Any(value => value.Name.Equals("WindowsInstaller", StringComparison.OrdinalIgnoreCase) && value.Data is int number && number == 1);
                            var name = Text("DisplayName");
                            if (string.IsNullOrWhiteSpace(name) || values.Any(value => value.Name.Equals("SystemComponent", StringComparison.OrdinalIgnoreCase) && value.Data is int number && number == 1) ||
                                !string.IsNullOrWhiteSpace(Text("ParentKeyName")) || !string.IsNullOrWhiteSpace(Text("ReleaseType")) ||
                                !IsStale(location, icon, command, msi, key, references, CleanerPaths.IsMissingLocalFile))
                            {
                                if (location != null) activeDirectories.Add(CleanerFileDeletion.ResolveReferencePath(location));
                                continue;
                            }
                            if (references.Busy || pending) continue;
                            var identity = $"registry:{sid}:{view32}:{path}";
                            bool directoryMissing = CleanerPaths.IsMissingLocalFile(location!);
                            var item = new LeftoverItem(Guid.NewGuid(), name, LeftoverKind.Registration, path, 0, true,
                                msi ? "The application executable is missing, and Windows Installer no longer registers this product." :
                                    "The application executable and uninstaller are missing.");
                            List<UninstallEvidence> evidence = [];
                            CaptureRegistration(account.Reader, path, sid, view32, item, evidence, 0, ct);
                            if (directoryMissing) candidates.Add(new(identity, item, evidence));
                            else if (UninstallResources.Under(icon!, location!) &&
                                (msi || command != null && UninstallResources.Under(command, location!)) &&
                                LeftoverDirectoryScanner.FindRoot(location!, sid, account, folders) is { } root)
                                directories.Add(new(name, root, evidence));
                        }
                        catch (Exception ex) when (UninstallResources.IsResourceError(ex))
                        { complete = false; notices.Add($"{path}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex))
            { complete = false; notices.Add($"{sid ?? "Machine"}: {ex.Message}"); }
        }
        if (!complete) notices.Add("Installer files and application folders were excluded because reference coverage is incomplete. Review the scan errors and scan again.");
        if (references.Busy || pending) notices.Add("An installation or restart is pending; installer files stay protected until it finishes.");
        var cutoff = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.AddDays(-30);
        if (complete && directoriesComplete && !references.Busy && !pending)
            candidates.AddRange(LeftoverDirectoryScanner.Read(directories, activeDirectories, references, cutoff, notices, ct));
        var roots = new[] { Path.Combine(folders.WindowsDirectory, "Installer") }
            .Concat(folders.ApplicationDirectories.Select(directory => Path.Combine(directory, "Package Cache")))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        foreach (var path in CleanerPaths.EnumerateFiles(root, notices, ct,
            recursive: !root.Equals(Path.Combine(folders.WindowsDirectory, "Installer"), StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".exe" && root.Equals(Path.Combine(folders.WindowsDirectory, "Installer"), StringComparison.OrdinalIgnoreCase)) continue;
            if (extension is not (".msi" or ".msp" or ".exe") ||
                references.Files.Contains(path) || references.SourceDirectories.Any(directory => UninstallResources.Under(path, directory))) continue;
            try
            {
                var file = new FileInfo(path);
                var reason = FileProtection(complete, references.Busy || pending, file.LastWriteTimeUtc, file.LastAccessTimeUtc, cutoff);
                if (reason != null) continue;
                var item = new LeftoverItem(Guid.NewGuid(), file.Name, extension switch
                { ".msi" => LeftoverKind.Msi, ".msp" => LeftoverKind.Msp, _ => LeftoverKind.Exe }, path, file.Length, true,
                    "No installed product, patch or uninstall registration references this cache file; it has not been used for at least 30 days.");
                var target = new CleanerFileTarget(Path.GetDirectoryName(path)!, path, file.Length, file.LastWriteTimeUtc, cutoff);
                var boundary = new UninstallFileRoot(path, null, null, root, SingleFile: true, InstallerCache: true);
                var finding = Finding(item, path, UninstallArtifactKind.File, null, item.Bytes, file.LastWriteTimeUtc, false);
                candidates.Add(new("file:" + path, item, [new(finding, boundary, target)]));
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{path}: {ex.Message}"); }
        }
        return new(candidates, notices.Distinct().ToArray());
    }

    /// <summary>Missing metadata, inaccessible files and live MSI registrations are never evidence of an obsolete app.</summary>
    internal static bool IsStale(string? location, string? executable, string? command, bool msi, string product,
        InstallerReferences references, Func<string, bool> missing) =>
        location != null && executable != null && Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) && missing(executable) &&
        (msi ? references.Complete && Guid.TryParse(product, out _) && !references.Products.Contains(product) : command != null && missing(command));

    internal static string? FileProtection(bool complete, bool busy, DateTime written, DateTime used, DateTime cutoff) =>
        !complete ? "Installer ownership could not be verified for every account." :
        busy ? "An installation or restart is pending." :
        written > cutoff || used > cutoff ? "The installer file is recent or was used within the last 30 days." : null;

    /// <summary>Captures every typed value and child key; deletion later removes only backed-up values and empty keys.</summary>
    private static void CaptureRegistration(ICleanerRegistryReader reader, string path, string? sid, bool view32,
        LeftoverItem item, List<UninstallEvidence> evidence, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 16 || evidence.Count > 4096) throw new IOException("The registration is too large for bounded leftover cleanup.");
        foreach (var value in reader.GetValues(path, view32, ct))
        {
            if (evidence.Count >= 4096) throw new IOException("The registration is too large for bounded leftover cleanup.");
            evidence.Add(new(Finding(item, path, UninstallArtifactKind.RegistryValue, value.Name, 0, null, view32),
                RegistryData: UninstallResources.Serialize(value), RegistryKind: (int)value.Kind, Sid: sid));
        }
        foreach (var child in reader.GetSubKeyNames(path, view32))
            CaptureRegistration(reader, path + "\\" + child, sid, view32, item, evidence, depth + 1, ct);
        if (evidence.Count >= 4096) throw new IOException("The registration is too large for bounded leftover cleanup.");
        evidence.Add(new(Finding(item, path, UninstallArtifactKind.RegistryKey, null, 0, null, view32), Sid: sid));
    }

    private static UninstallResidue Finding(LeftoverItem item, string path, UninstallArtifactKind kind, string? value,
        long bytes, DateTime? modified, bool view32) => new(Guid.NewGuid(), item.Id.ToString("N"), item.Name, "Leftover", kind,
            path, value, "", bytes, modified, view32, item.CanRemove, item.Reason);
}
