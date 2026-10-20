using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;

internal sealed record InstallerReferences(HashSet<string> Products, HashSet<string> Files,
    HashSet<string> SourceDirectories, bool Complete, bool Busy, IReadOnlyList<string> Notices);

/// <summary>Isolates native MSI inventory so policy tests never query the installed products on the test machine.</summary>
internal interface IInstallerInventory
{
    InstallerReferences Read(CancellationToken ct);
}

/// <summary>Inventories every product context and patch state without invoking installation, repair or Win32_Product.</summary>
internal sealed class WindowsInstallerInventory : IInstallerInventory
{
    private const uint Success = 0, NoMoreItems = 259, AllContexts = 7, AllPatchStates = 15;
    private readonly IInstallerProductProperties _properties;
    private readonly IInstallerSourceLists _sources;

    public WindowsInstallerInventory() : this(new WindowsInstallerProductProperties(), new WindowsInstallerSourceLists()) { }
    internal WindowsInstallerInventory(IInstallerProductProperties properties, IInstallerSourceLists? sources = null)
    {
        _properties = properties;
        _sources = sources ?? new WindowsInstallerSourceLists();
    }

    public InstallerReferences Read(CancellationToken ct)
    {
        HashSet<string> products = new(StringComparer.OrdinalIgnoreCase), files = new(StringComparer.OrdinalIgnoreCase),
            directories = new(StringComparer.OrdinalIgnoreCase);
        List<string> notices = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                ct.ThrowIfCancellationRequested();
                var product = new StringBuilder(39);
                var sid = new StringBuilder(256);
                uint sidLength = (uint)sid.Capacity;
                var code = MsiEnumProductsEx(null, "S-1-1-0", AllContexts, index, product, out var context, sid, ref sidLength);
                if (code == NoMoreItems) break;
                Check(code, "MSI product enumeration");
                products.Add(product.ToString());
                string? user = context == 4 ? null : sid.ToString();
                var installed = ReadProductReferences(product.ToString(), user, context, files, directories);
                ReadSources(product.ToString(), user, context, false, directories, ct);
                if (!installed) continue;
                for (uint patchIndex = 0; ; patchIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var patch = new StringBuilder(39);
                    var target = new StringBuilder(39);
                    var patchSid = new StringBuilder(256);
                    uint patchSidLength = (uint)patchSid.Capacity;
                    code = MsiEnumPatchesEx(product.ToString(), user, context, AllPatchStates, patchIndex,
                        patch, target, out var patchContext, patchSid, ref patchSidLength);
                    if (code == NoMoreItems) break;
                    Check(code, "MSI patch enumeration");
                    var buffer = new StringBuilder(32768);
                    uint length = (uint)buffer.Capacity;
                    Check(MsiGetPatchInfoEx(patch.ToString(), target.ToString(), patchContext == 4 ? null : patchSid.ToString(),
                        patchContext, "LocalPackage", buffer, ref length), "MSI patch cache lookup");
                    AddPath(buffer.ToString(), files);
                    ReadSources(patch.ToString(), patchContext == 4 ? null : patchSid.ToString(), patchContext, true, directories, ct);
                }
            }
        }
        catch (Exception ex) when (Uninstall.UninstallResources.IsResourceError(ex))
        { notices.Add($"Installer reference inventory is incomplete: {ex.Message}"); }
        var processes = Process.GetProcessesByName("msiexec");
        var busy = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return new(products, files, directories, notices.Count == 0, busy, notices);
    }

    /// <summary>Advertised products retain source references but have no installed cache or patches to query.</summary>
    internal bool ReadProductReferences(string product, string? sid, uint context, HashSet<string> files, HashSet<string> directories)
    {
        var state = _properties.Read(product, sid, context, "State");
        if (state == "1") return false;
        if (state != "5") throw new Win32Exception(13, $"MSI product {product} has an unrecognized installation state ({state}).");
        AddPath(_properties.Read(product, sid, context, "LocalPackage"), files);
        AddPath(_properties.Read(product, sid, context, "InstallSource"), directories, sourceDirectory: true);
        AddPath(_properties.Read(product, sid, context, "InstallLocation"), directories, sourceDirectory: true);
        return true;
    }

    /// <summary>Separates read-only native property calls from product-state policy.</summary>
    private sealed class WindowsInstallerProductProperties : IInstallerProductProperties
    {
        public string Read(string product, string? sid, uint context, string property)
        {
            var buffer = new StringBuilder(32768);
            uint length = (uint)buffer.Capacity;
            var result = MsiGetProductInfoEx(product, sid, context, property, buffer, ref length);
            Check(result, $"MSI product {product} property {property} (context {context})");
            return buffer.ToString();
        }
    }

    /// <summary>Includes alternative local repair sources, not only the most recently used installation source.</summary>
    internal void ReadSources(string id, string? sid, uint context, bool patch, HashSet<string> directories, CancellationToken ct)
    {
        bool readAnySource = false;
        foreach (uint type in new uint[] { 1, 2 }) // MSISOURCETYPE_NETWORK (also local directories), MSISOURCETYPE_URL
        for (uint index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            var buffer = new StringBuilder(32768);
            uint length = (uint)buffer.Capacity;
            var result = _sources.Enumerate(id, sid, context, type | (patch ? 0x40000000u : 0u), index, buffer, ref length);
            if (result == NoMoreItems) break;
            // Windows reports bad configuration for a registered product with no SourceList key.
            // Verify absence in the exact registration context; a damaged or unreadable list is still an error.
            if (result == 1610 && index == 0 && !readAnySource && !patch && _sources.IsProductSourceListAbsent(id, sid, context)) return;
            Check(result, $"MSI {(patch ? "patch" : "product")} {id} repair source inventory (context {context}, source type {type}, index {index}, error {result})");
            readAnySource = true;
            AddPath(buffer.ToString(), directories, sourceDirectory: true);
        }
    }

    private static void AddPath(string value, HashSet<string> paths, bool sourceDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (sourceDirectory && Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile) return;
        if (sourceDirectory && value.StartsWith(@"\\", StringComparison.Ordinal))
            throw new Win32Exception(13, "A network repair source cannot be resolved within the local cache inventory.");
        var path = Uninstall.UninstallResources.LocalPath(value);
        if (path == null) throw new Win32Exception(13, "Installer cache metadata contains an unresolved path.");
        paths.Add(CleanerFileDeletion.ResolveReferencePath(path));
    }

    private static void Check(uint result, string operation)
    {
        if (result != Success) throw new Win32Exception((int)result, $"{operation}: {new Win32Exception((int)result).Message}");
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumProductsExW", ExactSpelling = true)]
    private static extern uint MsiEnumProductsEx(string? product, string? sid, uint context, uint index,
        StringBuilder installedProduct, out uint installedContext, StringBuilder installedSid, ref uint sidLength);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoExW", ExactSpelling = true)]
    private static extern uint MsiGetProductInfoEx(string product, string? sid, uint context, string property, StringBuilder value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumPatchesExW", ExactSpelling = true)]
    private static extern uint MsiEnumPatchesEx(string? product, string? sid, uint context, uint states, uint index,
        StringBuilder patch, StringBuilder targetProduct, out uint targetContext, StringBuilder targetSid, ref uint sidLength);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetPatchInfoExW", ExactSpelling = true)]
    private static extern uint MsiGetPatchInfoEx(string patch, string product, string? sid, uint context, string property,
        StringBuilder value, ref uint length);
}

/// <summary>Native property boundary for deterministic product-state regression tests.</summary>
internal interface IInstallerProductProperties
{
    string Read(string product, string? sid, uint context, string property);
}
