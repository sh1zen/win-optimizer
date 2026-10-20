using WinOptimizer.Application.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Services;

namespace WinOptimizer.UI.Features.AdvancedTools.Services;

public class AutounattendXmlGeneratorService : IAutounattendXmlGeneratorService
{
    private readonly ICompatibleSettingsRegistry _compatibleSettingsRegistry;
    private readonly ISystemSettingsDiscoveryService _discoveryService;
    private readonly ILogService _logService;
    private readonly AutounattendScriptBuilder _scriptBuilder;
    private readonly IPowerShellRunner _powerShellRunner;
    private readonly ISelectedAppsProvider _selectedAppsProvider;

    public AutounattendXmlGeneratorService(
        ICompatibleSettingsRegistry compatibleSettingsRegistry,
        ISystemSettingsDiscoveryService discoveryService,
        ILogService logService,
        AutounattendScriptBuilder scriptBuilder,
        IPowerShellRunner powerShellRunner,
        ISelectedAppsProvider selectedAppsProvider)
    {
        _compatibleSettingsRegistry = compatibleSettingsRegistry;
        _discoveryService = discoveryService;
        _logService = logService;
        _scriptBuilder = scriptBuilder;
        _powerShellRunner = powerShellRunner;
        _selectedAppsProvider = selectedAppsProvider;
    }

    public async Task<string> GenerateFromCurrentSelectionsAsync(string outputPath,
        IReadOnlyList<ConfigurationItem>? selectedWindowsApps = null)
    {
        try
        {
            _logService.Log(LogLevel.Info, "Starting autounattend.xml generation");

            var apps = selectedWindowsApps
                ?? await _selectedAppsProvider.GetSelectedWindowsAppsAsync();

            var config = await CreateConfigurationFromSystemAsync(apps);

            return await RenderConfigToXmlAsync(config, outputPath);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error generating autounattend.xml: {ex.Message}");
            throw;
        }
    }

    public async Task<string> GenerateFromConfigAsync(UnifiedConfigurationFile config, string outputPath)
    {
        try
        {
            _logService.Log(LogLevel.Info, "Starting autounattend.xml generation from Builder config");
            return await RenderConfigToXmlAsync(config, outputPath);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error generating autounattend.xml from config: {ex.Message}");
            throw;
        }
    }

    private async Task<string> RenderConfigToXmlAsync(UnifiedConfigurationFile config, string outputPath)
    {
        var allSettings = _compatibleSettingsRegistry.GetAllFilteredSettings();

        var scriptContent = await _scriptBuilder.BuildOptimizationsScriptAsync(config, allSettings);

        var xmlTemplate = LoadEmbeddedTemplate();

        var finalXml = InjectScriptIntoTemplate(xmlTemplate, scriptContent);

        // Validate the final XML is well-formed
        try
        {
            await _powerShellRunner.ValidateXmlSyntaxAsync(finalXml);
            _logService.Log(LogLevel.Info, "autounattend.xml passed XML well-formedness validation");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"autounattend.xml failed XML well-formedness validation: {ex.Message}");
            throw;
        }

        // Write without BOM (Byte Order Mark) - Windows Setup requires UTF-8 without BOM
        var utf8WithoutBom = new UTF8Encoding(false);
        await File.WriteAllTextAsync(outputPath, finalXml, utf8WithoutBom);

        _logService.Log(LogLevel.Info, $"Autounattend.xml generated successfully: {outputPath}");
        return outputPath;
    }

    private async Task<UnifiedConfigurationFile> CreateConfigurationFromSystemAsync(
        IReadOnlyList<ConfigurationItem>? selectedWindowsApps)
    {
        var config = new UnifiedConfigurationFile
        {
            Version = "2.0",
            CreatedAt = DateTime.UtcNow
        };

        await FeatureConfigurationSnapshot.PopulateAsync(
            config, _compatibleSettingsRegistry, _discoveryService, _logService, CreateConfigurationItem);
        PopulateAppsSections(config, selectedWindowsApps);

        return config;
    }

    /// <summary>Projects discovered state into raw power values for answer-file generation.</summary>
    private ConfigurationItem CreateConfigurationItem(SettingDefinition setting, SettingStateResult? state)
    {
        var item = new ConfigurationItem
        {
            Id = setting.Id,
            Name = setting.Name,
            InputType = setting.InputType
        };

        if (setting.InputType == InputType.Toggle)
        {
            item.IsSelected = state?.IsEnabled ?? false;
        }
        else if (setting.InputType == InputType.Selection)
        {
            var (selectedIndex, powerPlanGuid, powerPlanName) = GetSelectionStateFromState(setting, state);

            if (setting.Id == SettingIds.PowerPlanSelection)
            {
                item.PowerPlanGuid = powerPlanGuid;
                item.PowerPlanName = powerPlanName;
            }
            else
            {
                item.SelectedIndex = selectedIndex;
            }
        }

        // Keep both raw modes in the snapshot: one selection index only describes AC.
        if (setting.PowerCfgSettings?.Any() == true && state?.RawValues != null)
        {
            var powerValues = state.RawValues
                .Where(value => value.Key is "ACValue" or "DCValue" && value.Value != null)
                .ToDictionary(value => value.Key, value => value.Value!);
            if (powerValues.Count > 0)
            {
                item.PowerSettings = powerValues;
                item.SelectedIndex = null;
            }
        }

        if (setting.InputType == InputType.Selection &&
            item.SelectedIndex == null &&
            item.PowerSettings == null &&
            setting.Id != SettingIds.PowerPlanSelection &&
            state?.RawValues != null && state.RawValues.Count > 0)
        {
            item.CustomStateValues = state.RawValues
                .Where(v => v.Value != null)
                .ToDictionary(k => k.Key, v => v.Value!);
        }

        return item;
    }

    private void PopulateAppsSections(UnifiedConfigurationFile config,
        IReadOnlyList<ConfigurationItem>? selectedWindowsApps)
    {
        if (selectedWindowsApps != null && selectedWindowsApps.Count > 0)
        {
            config.WindowsApps.IsIncluded = true;
            config.WindowsApps.Items = selectedWindowsApps.ToList();
            _logService.Log(LogLevel.Info, $"Exported {config.WindowsApps.Items.Count} checked Windows Apps");
        }
    }

    private (int? selectedIndex, string? powerPlanGuid, string? powerPlanName)
        GetSelectionStateFromState(SettingDefinition setting, SettingStateResult? state)
    {
        if (state?.CurrentValue is not int index)
            return (0, null, null);

        if (setting.Id == SettingIds.PowerPlanSelection && state.RawValues != null)
        {
            var guid = state.RawValues.TryGetValue("ActivePowerPlanGuid", out var g) ? g?.ToString() : null;
            var name = state.RawValues.TryGetValue("ActivePowerPlan", out var n) ? n?.ToString() : null;

            _logService.Log(LogLevel.Info, $"[AutounattendXmlGeneratorService] Exporting power plan: {name} ({guid})");
            return (index, guid, name);
        }

        return (index == ComboBoxConstants.CustomStateIndex ? null : index, null, null);
    }

    private string LoadEmbeddedTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "WinOptimizer.UI.Resources.AdvancedTools.autounattend-template.xml";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Embedded template not found: {resourceName}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string InjectScriptIntoTemplate(string template, string scriptContent)
    {
        const string placeholder = "<!--SCRIPT_PLACEHOLDER-->";
        const string replacement = "<![CDATA[{0}]]>";

        if (!template.Contains(placeholder))
            throw new InvalidOperationException("Script placeholder not found in template");

        return template.Replace(placeholder, string.Format(replacement, scriptContent));
    }
}
