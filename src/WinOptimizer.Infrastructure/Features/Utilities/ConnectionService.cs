using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Collects adapter and VPN state without changing connections or starting background work.</summary>
internal sealed class ConnectionService(IConnectionAdapterSource adapters, IInteractiveUserService user,
    IApplicationModeService mode) : IConnectionService
{
    // Always emit arrays, including an empty phone book, and query both scopes independently.
    internal const string VpnScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $profiles = @(); $diagnostics = @()
        foreach ($all in @($false, $true)) {
            try {
                $profiles += @(Get-VpnConnection -AllUserConnection:$all -ErrorAction Stop | ForEach-Object {
                    [pscustomobject]@{ Name = $_.Name; Server = $_.ServerAddress; Status = [string]$_.ConnectionStatus;
                        AllUsers = $all; SplitTunneling = [bool]$_.SplitTunneling }
                })
            } catch { $diagnostics += ('AllUsers=' + $all + ': ' + $_.Exception.Message) }
        }
        [pscustomobject]@{ Profiles = @($profiles); Diagnostics = @($diagnostics) } | ConvertTo-Json -Depth 4 -Compress
        """;

    public async Task<ConnectionSnapshot> ReadAsync()
    {
        var readings = await Task.Run(adapters.Read).ConfigureAwait(false);
        IReadOnlyList<ConnectionVpn> profiles = [];
        var diagnostic = "";
        try
        {
            if (user.IsOtsElevation && !user.HasInteractiveUserToken)
                throw new UtilityOperationException("The interactive user's VPN profiles cannot be read without an interactive user token.");
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(VpnScript));
            var result = await user.RunProcessAsInteractiveUserAsync(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -EncodedCommand " + encoded, timeoutMs: 15000).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new UtilityOperationException("Windows VPN discovery failed. " + result.StandardError);
            using var json = JsonDocument.Parse(result.StandardOutput);
            profiles = json.RootElement.GetProperty("Profiles").EnumerateArray().Select(profile => new ConnectionVpn(
                profile.GetProperty("Name").GetString() ?? "", profile.GetProperty("Server").GetString() ?? "",
                profile.GetProperty("Status").GetString() ?? "", profile.GetProperty("AllUsers").GetBoolean(),
                profile.GetProperty("SplitTunneling").GetBoolean())).ToArray();
            diagnostic = string.Join(Environment.NewLine, json.RootElement.GetProperty("Diagnostics").EnumerateArray()
                .Select(value => value.GetString()));
        }
        catch (OperationCanceledException)
        { diagnostic = "Windows VPN discovery timed out after 15 seconds. Reload to try again."; }
        catch (Exception ex) when (ex is UtilityOperationException or JsonException or KeyNotFoundException or
            InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        { diagnostic = "VPN: " + ex.Message; }
        return new(DateTimeOffset.Now, readings, profiles, diagnostic);
    }

    public Task OpenVpnSettingsAsync()
    {
        using var reservation = mode.BeginSystemOperation();
        if (mode.CurrentMode != WinOptimizerMode.Normal)
            throw new InvalidOperationException("VPN management requires normal application mode.");
        if (user.IsOtsElevation && !user.HasInteractiveUserToken)
            throw new UtilityOperationException("Windows VPN settings require the interactive user token. Start the app as the signed-in user.");
        user.LaunchProcessAsInteractiveUser("explorer.exe", "ms-settings:network-vpn");
        return Task.CompletedTask;
    }
}
