using System.ComponentModel;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Security;

namespace WinOptimizer.Infrastructure.Features.Security;

/// <summary>Uses isolated Windows queries and delegates existing preferences to their setting owner.</summary>
public sealed class WindowsSecurityBackend(IProcessExecutor processes, IInteractiveUserService user,
    ICompatibleSettingsRegistry catalog, ISystemSettingsDiscoveryService discovery,
    ISettingApplicationService settings) : ISecurityBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true
    };

    private static string? SettingId(SecurityCheckId id) => id switch
    {
        SecurityCheckId.LanguageList => "privacy-language-list",
        SecurityCheckId.AppTracking => "privacy-app-launch-tracking",
        _ => null
    };

    /// <summary>Joins native observations with the original catalogue's current privacy-setting states.</summary>
    public async Task<IReadOnlyList<SecurityObservation>> ReadAsync(IReadOnlyList<SecurityCheckId> ids, CancellationToken token)
    {
        if (ids.Any(id => !Enum.IsDefined(id)) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Unsupported or duplicate security check.", nameof(ids));
        var native = ids.Where(id => SettingId(id) == null).ToArray();
        var results = native.Length == 0 ? new List<SecurityObservation>() : await RunAsync(native, null, token).ConfigureAwait(false);
        var preferences = ids.Where(id => SettingId(id) != null).Select(id => (Id: id, Definition: catalog.GetById(SettingId(id)!))).ToArray();
        var definitions = preferences.Select(preference => preference.Definition).OfType<SettingDefinition>().ToArray();
        var states = definitions.Length == 0 ? new Dictionary<string, SettingStateResult>() :
            await discovery.GetSettingStatesAsync(definitions).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        foreach (var (id, definition) in preferences)
        {
            if (definition == null) { results.Add(new(id, SecurityCheckState.NotApplicable, "", "Setting unavailable on this Windows version.", false)); continue; }
            if (!states.TryGetValue(definition.Id, out var state) || !state.Success)
                results.Add(new(id, SecurityCheckState.Unknown, "", state?.ErrorMessage ?? "Setting state unavailable.", false));
            else
                results.Add(new(id, state.IsEnabled ? SecurityCheckState.Attention : SecurityCheckState.Good,
                    state.IsEnabled ? "Enabled" : "Disabled", state.IsEnabled.ToString(), state.IsEnabled));
        }
        return ids.Select(id => results.Single(item => item.Id == id)).ToArray();
    }

    /// <summary>Compares scan evidence before mutation and requires successful native or setting readback.</summary>
    public async Task FixAsync(SecurityObservation expected, CancellationToken token)
    {
        if (!expected.CanFix || expected.State != SecurityCheckState.Attention)
            throw new ArgumentException("This finding cannot authorize a security fix.", nameof(expected));
        if (SettingId(expected.Id) is { } settingId)
        {
            var current = (await ReadAsync([expected.Id], token).ConfigureAwait(false)).Single();
            if (current != expected) throw new InvalidOperationException("The setting changed after scanning. Scan again.");
            // The setting owner completes its atomic unit before cancellation is observed.
            var result = await settings.ApplySettingAsync(new() { SettingId = settingId, Enable = false }).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException(result.ErrorMessage ?? "The preference could not be changed.", result.Exception);
            var updated = (await ReadAsync([expected.Id], CancellationToken.None).ConfigureAwait(false)).Single();
            if (updated.State != SecurityCheckState.Good) throw new InvalidOperationException("The preference change was not confirmed. Scan again.");
        }
        else
            await RunAsync([expected.Id], expected, token).ConfigureAwait(false);
    }

    /// <summary>Encodes request data separately from the embedded script and waits for a write unit to finish.</summary>
    private async Task<List<SecurityObservation>> RunAsync(SecurityCheckId[] ids, SecurityObservation? fix, CancellationToken token)
    {
        using var identity = user.IsOtsElevation ? null : WindowsIdentity.GetCurrent();
        var sid = user.IsOtsElevation ? user.InteractiveUserSid : identity?.User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) throw new InvalidOperationException("The interactive user identity is unavailable.");
        sid = new SecurityIdentifier(sid).Value;
        using var resource = typeof(WindowsSecurityBackend).Assembly.GetManifestResourceStream("WinOptimizer.Security.Security.ps1")
            ?? throw new InvalidOperationException("The Windows security query is missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var request = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Ids = ids.Select(id => id.ToString()).ToArray(), Sid = sid, Fix = fix
        }, JsonOptions)));
        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);\n" +
            "$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + request + "')) | ConvertFrom-Json\n" +
            await reader.ReadToEndAsync(token).ConfigureAwait(false);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (encoded.Length > 30000) throw new InvalidOperationException("The Windows security query exceeds the process command limit.");
        ProcessExecutionResult output;
        try
        {
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            token.ThrowIfCancellationRequested();
            output = await processes.ExecuteWithStreamingAsync(executable, "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                ct: fix == null ? token : CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Windows security query failed: " + ex.Message, ex); }
        if (!output.Succeeded) throw new InvalidOperationException("Windows security operation failed: " + output.StandardError.Trim());
        try
        {
            var rows = JsonSerializer.Deserialize<List<SecurityObservation>>(output.StandardOutput.Trim().TrimStart('\uFEFF'), JsonOptions)
                ?? throw new JsonException("No security data returned.");
            if (rows.Count != ids.Length || rows.Select(row => row.Id).Distinct().Count() != ids.Length ||
                rows.Any(row => !ids.Contains(row.Id) || !Enum.IsDefined(row.State) || row.Current == null || row.Evidence == null ||
                    row.CanFix && row.State != SecurityCheckState.Attention))
                throw new JsonException("Invalid security report contract.");
            return rows;
        }
        catch (JsonException ex) { throw new InvalidOperationException("Windows returned unreadable security data.", ex); }
    }
}
