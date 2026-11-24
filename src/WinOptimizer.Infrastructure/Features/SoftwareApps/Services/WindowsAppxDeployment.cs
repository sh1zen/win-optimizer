using System.Security.Principal;
using System.Text;
using Windows.Management.Deployment;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Immutable package identities captured for one account inventory.</summary>
public sealed record AppxPackageIdentity(string Name, string FullName);

/// <summary>Isolates account selection, package enumeration and per-account deployment calls.</summary>
public interface IAppxDeployment
{
    /// <summary>Explicit SID under alternate-account elevation; empty means the calling account.</summary>
    string InteractiveAccountSid { get; }
    Task<IReadOnlyList<AppxPackageIdentity>> GetPackagesAsync(string accountSid, CancellationToken ct);
    async Task<(IReadOnlyList<AppxPackageIdentity> Packages, IReadOnlyList<AppxRegistration>? Registrations)> ReadAccountInventoryAsync(
        string accountSid, CancellationToken ct) => (await GetPackagesAsync(accountSid, ct).ConfigureAwait(false), null);
    Task RemovePackageAsync(string fullName, string accountSid, Action<int> progress, CancellationToken ct);
}

/// <summary>Uses WinRT for the calling account and explicit SID removal for another signed-in account.</summary>
public sealed class WindowsAppxDeployment(IInteractiveUserService user, IProcessExecutor processes,
    ILogService log) : IAppxDeployment
{
    public string InteractiveAccountSid
    {
        get
        {
            if (!user.IsOtsElevation) return "";
            var sid = user.InteractiveUserSid;
            if (string.IsNullOrWhiteSpace(sid)) throw new IOException("The interactive Windows account could not be identified.");
            return new SecurityIdentifier(sid).Value;
        }
    }

    /// <summary>Reads a materialized account snapshot on a worker, respecting cancellation before Windows access.</summary>
    public async Task<IReadOnlyList<AppxPackageIdentity>> GetPackagesAsync(string accountSid, CancellationToken ct) =>
        (await ReadAccountInventoryAsync(accountSid, includeRegistrations: false, ct).ConfigureAwait(false)).Packages;

    public Task<(IReadOnlyList<AppxPackageIdentity> Packages, IReadOnlyList<AppxRegistration>? Registrations)> ReadAccountInventoryAsync(
        string accountSid, CancellationToken ct) => ReadAccountInventoryAsync(accountSid, includeRegistrations: true, ct);

    internal static AppxRegistrationDetails ReadRegistrationDetails(Windows.ApplicationModel.Package package) =>
        new(package.Id.FullName, package.Id.FamilyName, package.InstalledDate, package.InstalledLocation.Path, package.IsDevelopmentMode);

    private Task<(IReadOnlyList<AppxPackageIdentity> Packages, IReadOnlyList<AppxRegistration>? Registrations)> ReadAccountInventoryAsync(
        string accountSid, bool includeRegistrations, CancellationToken ct) =>
        Task.Run<(IReadOnlyList<AppxPackageIdentity>, IReadOnlyList<AppxRegistration>?)>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (accountSid.Length > 0) _ = new SecurityIdentifier(accountSid);
            List<AppxPackageIdentity> packages = [];
            List<AppxRegistration>? registrations = includeRegistrations ? [] : null;
            foreach (var package in new PackageManager().FindPackagesForUser(accountSid))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var id = package.Id;
                    var name = id.Name;
                    packages.Add(new(name, id.FullName));
                    registrations?.Add(new(name, () => ReadRegistrationDetails(package)));
                }
                catch (Exception ex)
                {
                    if (accountSid.Length > 0) throw new IOException($"Cannot read a package for account {accountSid}.", ex);
                    log.LogWarning($"PackageManager enumeration skipped an entry: {ex.Message}");
                    registrations = null; // A partial identity inventory cannot replace the independent metadata read.
                }
            }
            return (packages, registrations);
        }, ct);

    /// <summary>Removes exactly one captured package registration and surfaces native or PowerShell failures.</summary>
    public async Task RemovePackageAsync(string fullName, string accountSid, Action<int> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        if (accountSid.Length > 0)
        {
            var sid = new SecurityIdentifier(accountSid).Value;
            var script = $"Remove-AppxPackage -Package '{fullName.Replace("'", "''")}' -User '{sid}' -ErrorAction Stop";
            var arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var executable = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var result = await processes.ExecuteAsync(executable, arguments, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!result.Succeeded)
                throw new IOException($"AppX removal of {fullName} for {accountSid} failed (exit {result.ExitCode}): {result.StandardError}");
            return;
        }

        var operation = new PackageManager().RemovePackageAsync(fullName);
        operation.Progress = (_, value) => progress((int)value.percentage);
        using var cancellation = ct.Register(() => operation.Cancel());
        var deployment = await operation.AsTask().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (deployment.ExtendedErrorCode is { } error)
            throw new IOException(string.IsNullOrEmpty(deployment.ErrorText) ? error.Message : deployment.ErrorText, error);
    }
}
