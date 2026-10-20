using System.ComponentModel;
using System.Globalization;
using System.Management;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Native;
using WinOptimizer.Infrastructure.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Reads Windows' restore inventory and removes an explicitly identified restore point.</summary>
public sealed class WindowsRestorePointStore : ISystemRestorePointStore
{
    public IReadOnlyList<SystemRestorePoint> Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(@"root\default",
            "SELECT SequenceNumber, Description, CreationTime FROM SystemRestore");
        using var results = searcher.Get();
        List<SystemRestorePoint> points = [];
        foreach (ManagementObject point in results)
        {
            using (point)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sequence = Convert.ToUInt32(point["SequenceNumber"], CultureInfo.InvariantCulture);
                var created = point["CreationTime"] as string;
                if (sequence == 0 || string.IsNullOrWhiteSpace(created))
                    throw new InvalidDataException("Windows returned a restore point without its identifier or creation time.");
                points.Add(new(sequence, point["Description"] as string ?? "",
                    new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(created))));
            }
        }
        return points;
    }

    public void Delete(uint sequenceNumber)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sequenceNumber);
        var status = SrClientApi.RemoveRestorePoint(sequenceNumber);
        if (status != 0)
            throw new Win32Exception(checked((int)status), $"Windows could not delete restore point #{sequenceNumber}: {new Win32Exception(checked((int)status)).Message}");
    }
}
