using WinOptimizer.Core.Features.Common.Constants;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Runs embedded, fixed scripts; request data is JSON, never executable command text.</summary>
internal sealed class AutorunPowerShellBackend(IProcessExecutor processes, string? temporaryDirectory = null) : IAutorunBackend
{
    private readonly ManagementPowerShellRunner _shell = new(processes, temporaryDirectory);
    private static readonly JsonSerializerOptions JsonOptions = new() { RespectRequiredConstructorParameters = true };

    public async Task<AutorunInventory> ReadAsync(AutorunView view, bool acceptLicense, CancellationToken cancellationToken)
    {
        var result = await _shell.RunAsync(view == AutorunView.Advanced ? "Scan-Autoruns" : "Startup", new
        {
            Action = "Read", AcceptLicense = acceptLicense,
            ToolDirectory = Path.Combine(AppDataPaths.Tools, "autoruns")
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            if (view == AutorunView.Advanced)
            {
                var advanced = JsonSerializer.Deserialize<AdvancedData>(result, JsonOptions)
                    ?? throw new JsonException("Empty advanced inventory.");
                if (advanced.Csv == null || advanced.Warnings == null || advanced.Warnings.Any(warning => warning == null) || advanced.Coverage == null)
                    throw new JsonException("Missing advanced inventory fields.");
                return new(view, DateTimeOffset.Now, AutorunCsvReader.Read(advanced.Csv), advanced.Warnings, advanced.Coverage);
            }
            var data = JsonSerializer.Deserialize<InventoryData>(result, JsonOptions)
                ?? throw new JsonException("Empty inventory.");
            if (data.Entries == null || data.Warnings == null || data.Coverage == null ||
                data.Entries.Any(entry => entry == null || entry.Id == null || entry.Name == null || entry.Category == null ||
                    entry.Location == null || entry.Command == null || entry.ImagePath == null || entry.Publisher == null ||
                    entry.Signer == null || entry.Profile == null || entry.Details == null) || data.Warnings.Any(warning => warning == null))
                throw new JsonException("Missing inventory fields.");
            return new(view, DateTimeOffset.Now, data.Entries, data.Warnings, data.Coverage);
        }
        catch (JsonException ex) { throw new UtilityOperationException("Windows returned an invalid startup inventory.", ex); }
    }

    public async Task SetEnabledAsync(AutorunEntry entry, bool enabled)
    {
        var output = await _shell.RunAsync("Startup", new { Action = "Write", Entry = entry, Enabled = enabled }, CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(output);
            if (!json.RootElement.GetProperty("Applied").GetBoolean()) throw new JsonException("The change was not acknowledged.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new UtilityOperationException("Windows did not acknowledge the startup change. Refresh to verify its state.", ex); }
    }

    public async Task OpenStartupSettingsAsync()
    {
        if (await processes.ShellExecuteAsync("ms-settings:startupapps").ConfigureAwait(false) == null)
            throw new UtilityOperationException("Windows Startup settings could not be opened.");
    }

    private sealed record InventoryData(AutorunEntry[] Entries, string[] Warnings, string Coverage);
    private sealed record AdvancedData(string Csv, string[] Warnings, string Coverage);
}
