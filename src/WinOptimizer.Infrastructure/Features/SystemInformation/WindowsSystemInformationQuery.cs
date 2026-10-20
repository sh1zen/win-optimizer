using System.Management;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.SystemInformation.Models;

namespace WinOptimizer.Infrastructure.Features.SystemInformation;

/// <summary>Runs bounded, read-only local WMI queries on a worker thread and releases native objects.</summary>
public sealed class WindowsSystemInformationQuery : ISystemInformationQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(string className,
        IReadOnlyList<string> properties, CancellationToken ct) => Task.Run<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(() =>
    {
        // Only identifiers are accepted; callers cannot inject WQL clauses or namespaces.
        if (!IsIdentifier(className) || properties.Count == 0 || properties.Any(property => !IsIdentifier(property)))
            throw new ArgumentException("Invalid system information query identifiers.");
        ct.ThrowIfCancellationRequested();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery($"SELECT {string.Join(",", properties)} FROM {className}"),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(15) });
            using var collection = searcher.Get();
            List<IReadOnlyDictionary<string, object?>> rows = [];
            foreach (ManagementObject entry in collection)
            {
                using (entry)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in properties) row.Add(property, entry[property]);
                    rows.Add(row);
                }
            }
            ct.ThrowIfCancellationRequested();
            return rows;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        { throw new SystemInformationException($"{className}: {ex.Message}", ex); }
    }, ct);

    private static bool IsIdentifier(string value) => !string.IsNullOrEmpty(value) &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
