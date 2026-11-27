using System.Management;
using System.Text.RegularExpressions;

namespace WinOptimizer.Infrastructure.Features.Disks;

internal sealed record ShadowCopy(Guid Id, DateTime Created, string Root);

internal interface IShadowCopyCatalog
{
    IEnumerable<ShadowCopy> List(string volumeId, CancellationToken ct);
}

/// <summary>Enumerates trusted VSS device roots for the requested volume GUID.</summary>
internal sealed partial class WindowsShadowCopyCatalog : IShadowCopyCatalog
{
    public IEnumerable<ShadowCopy> List(string volumeId, CancellationToken ct)
    {
        using var query = new ManagementObjectSearcher("SELECT ID, InstallDate, VolumeName, DeviceObject FROM Win32_ShadowCopy");
        using var rows = query.Get();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(row["VolumeName"] as string, volumeId, StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(row["ID"] as string, out var id) && row["InstallDate"] is string date &&
                    row["DeviceObject"] is string device && ShadowDevicePattern().IsMatch(device))
                    yield return new(id, ManagementDateTimeConverter.ToDateTime(date), device + '\\');
            }
        }
    }

    [GeneratedRegex(@"\A\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy[0-9]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShadowDevicePattern();
}
