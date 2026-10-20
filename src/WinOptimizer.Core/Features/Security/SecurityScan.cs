using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Core.Features.Security;

public enum SecurityArea { Antivirus, Firewall, Spyware, Weaknesses, Updates }
public enum SecurityCheckId { Antivirus, Firewall, Spyware, Uac, Updates, Isatap, Teredo, Firefox, LanguageList, AppTracking }
public enum SecurityCheckState { Good, Attention, Unknown, NotApplicable }

/// <summary>Identifies supported checks and their category without duplicating Windows settings.</summary>
public sealed record SecurityCheck(SecurityCheckId Id, SecurityArea Area, bool DeepOnly = false)
{
    public static IReadOnlyList<SecurityCheck> All { get; } = Array.AsReadOnly<SecurityCheck>([
        new(SecurityCheckId.Antivirus, SecurityArea.Antivirus),
        new(SecurityCheckId.Firewall, SecurityArea.Firewall),
        new(SecurityCheckId.Spyware, SecurityArea.Spyware),
        new(SecurityCheckId.Uac, SecurityArea.Weaknesses),
        new(SecurityCheckId.Updates, SecurityArea.Updates),
        new(SecurityCheckId.Isatap, SecurityArea.Weaknesses, true),
        new(SecurityCheckId.Teredo, SecurityArea.Weaknesses, true),
        new(SecurityCheckId.Firefox, SecurityArea.Weaknesses, true),
        new(SecurityCheckId.LanguageList, SecurityArea.Weaknesses, true),
        new(SecurityCheckId.AppTracking, SecurityArea.Weaknesses, true)
    ]);
}

/// <summary>Retains exact native evidence; unavailable checks cannot authorize a fix.</summary>
public sealed record SecurityObservation(SecurityCheckId Id, SecurityCheckState State, string Current, string Evidence, bool CanFix);
public sealed record SecurityScanReport(Guid Id, DateTimeOffset ScannedAt, bool Deep, IReadOnlyList<SecurityObservation> Items);
public sealed record SecurityFixOutcome(SecurityCheckId Id, bool Fixed, string Error);
public sealed record SecurityFixResult(IReadOnlyList<SecurityFixOutcome> Outcomes, bool Cancelled);

/// <summary>Isolates Windows discovery and narrowly scoped changes from scan authority.</summary>
public interface ISecurityBackend
{
    Task<IReadOnlyList<SecurityObservation>> ReadAsync(IReadOnlyList<SecurityCheckId> ids, CancellationToken token);
    Task FixAsync(SecurityObservation expected, CancellationToken token);
}

public interface ISecurityScanService
{
    Task<SecurityScanReport> ScanAsync(bool deep, CancellationToken token);
    Task<SecurityFixResult> FixAsync(Guid reportId, IReadOnlyList<SecurityCheckId> ids, CancellationToken token);
}

/// <summary>Serializes scans and fixes, retaining the authoritative report and consuming selected evidence.</summary>
public sealed class SecurityScanService(ISecurityBackend backend, IApplicationModeService mode) : ISecurityScanService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SecurityScanReport? _report;
    private readonly HashSet<SecurityCheckId> _consumed = [];

    /// <summary>Publishes only a complete supported check set and invalidates prior authority before reading.</summary>
    public async Task<SecurityScanReport> ScanAsync(bool deep, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _report = null;
            _consumed.Clear();
            var ids = SecurityCheck.All.Where(check => deep || !check.DeepOnly).Select(check => check.Id).ToArray();
            var items = await backend.ReadAsync(ids, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (items.Count != ids.Length || items.Select(item => item.Id).Distinct().Count() != ids.Length ||
                items.Any(item => !ids.Contains(item.Id)))
                throw new InvalidOperationException("Windows returned an incomplete security report. Scan again.");
            return _report = new(Guid.NewGuid(), DateTimeOffset.Now, deep, Array.AsReadOnly(items.ToArray()));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Validates every selection before writes; cancellation retains per-item outcomes and consumed identities.</summary>
    public async Task<SecurityFixResult> FixAsync(Guid reportId, IReadOnlyList<SecurityCheckId> ids, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var selected = ids.ToArray();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (mode.CurrentMode != WinOptimizerMode.Normal)
                throw new InvalidOperationException("Security fixes require normal application mode.");
            using var reservation = mode.BeginSystemOperation();
            if (_report?.Id != reportId || selected.Length == 0 || selected.Distinct().Count() != selected.Length)
                throw new ArgumentException("Select findings from the current security scan.");
            var known = _report.Items.ToDictionary(item => item.Id);
            if (selected.Any(id => !known.TryGetValue(id, out var item) || _consumed.Contains(id) ||
                !item.CanFix || item.State != SecurityCheckState.Attention))
                throw new ArgumentException("A selected finding cannot be fixed. Scan again.");
            var outcomes = new List<SecurityFixOutcome>();
            foreach (var id in selected)
            {
                if (token.IsCancellationRequested) return new(outcomes.AsReadOnly(), true);
                _consumed.Add(id);
                try
                {
                    // The backend compares fresh evidence immediately before its write and verifies readback.
                    await backend.FixAsync(known[id], token).ConfigureAwait(false);
                    outcomes.Add(new(id, true, ""));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { return new(outcomes.AsReadOnly(), true); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                { outcomes.Add(new(id, false, ex.Message)); }
            }
            return new(outcomes.AsReadOnly(), false);
        }
        finally { _gate.Release(); }
    }
}
