namespace WinOptimizer.Infrastructure.Features.SystemInformation;

/// <summary>The read-only WMI boundary; property values are normalized by the inventory owner.</summary>
public interface ISystemInformationQuery
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(string className,
        IReadOnlyList<string> properties, CancellationToken ct);
}
