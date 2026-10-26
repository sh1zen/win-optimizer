using System.Security.Principal;
using System.Text;
using Windows.Management.Deployment;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Immutable package identities captured for one account inventory.</summary>
public sealed record AppxPackageIdentity(string Name, string FullName);

/// <summary>Isolates account selection, package enumeration and per-account deployment calls.</summary>
public interface IAppxDeployment
{
    /// <summary>Explicit SID under alternate-account elevation; empty means the calling account.</summary>
    string InteractiveAccountSid { get; }
    Task<IReadOnlyList<AppxPackageIdentity>> GetPackagesAsync(string accountSid, CancellationToken ct);
    Task RemovePackageAsync(string fullName, string accountSid, Action<int> progress, CancellationToken ct);
    Task InstallPackageAsync(string packagePath, IReadOnlyList<string> dependencyPaths, CancellationToken ct);
}

/// <summary>Uses WinRT for the calling account and explicit SID removal for another signed-in account.</summary>
public sealed class WindowsAppxDeployment(IInteractiveUserService user, IProcessExecutor processes,
    ILogService log) : IAppxDeployment
{
    /// <summary>Registers downloaded packages for the interactive account, refusing another-account fallback.</summary>
    public async Task InstallPackageAsync(string packagePath, IReadOnlyList<string> dependencyPaths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = AppPaths.ResolveDetectionPath(packagePath, user);
        var dependencies = dependencyPaths.Select(dependency => AppPaths.ResolveDetectionPath(dependency, user)).ToArray();
        var accountSid = InteractiveAccountSid;
        if (accountSid.Length > 0)
        {
            if (!user.HasInteractiveUserToken)
                throw new IOException("Store installation requires the interactive user's token. Restart the app as the signed-in user.");
            var script = $"Add-AppxPackage -Path '{path.Replace("'", "''")}' -ErrorAction Stop";
            if (dependencies.Length > 0)
                script += " -DependencyPath @(" + string.Join(",", dependencies.Select(dependency => "'" + dependency.Replace("'", "''") + "'")) + ")";
            var arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var result = await user.RunProcessAsInteractiveUserAsync(
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), arguments,
                cancellationToken: ct, timeoutMs: Timeout.Infinite).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
                throw new IOException($"Store installation for {accountSid} failed (exit {result.ExitCode}): {result.StandardError} {result.StandardOutput}".Trim());
            return;
        }

        var deployment = await new PackageManager().AddPackageAsync(new Uri(path), dependencies.Select(dependency => new Uri(dependency)),
            DeploymentOptions.None).AsTask(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (deployment.ExtendedErrorCode is { } error)
            throw new IOException(string.IsNullOrEmpty(deployment.ErrorText) ? error.Message : deployment.ErrorText, error);
    }

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
    public Task<IReadOnlyList<AppxPackageIdentity>> GetPackagesAsync(string accountSid, CancellationToken ct) =>
        Task.Run<IReadOnlyList<AppxPackageIdentity>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (accountSid.Length > 0) _ = new SecurityIdentifier(accountSid);
            List<AppxPackageIdentity> packages = [];
            foreach (var package in new PackageManager().FindPackagesForUser(accountSid))
            {
                ct.ThrowIfCancellationRequested();
                try { packages.Add(new(package.Id.Name, package.Id.FullName)); }
                catch (Exception ex)
                {
                    if (accountSid.Length > 0) throw new IOException($"Cannot read a package for account {accountSid}.", ex);
                    log.LogWarning($"PackageManager enumeration skipped an entry: {ex.Message}");
                }
            }
            return packages;
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
