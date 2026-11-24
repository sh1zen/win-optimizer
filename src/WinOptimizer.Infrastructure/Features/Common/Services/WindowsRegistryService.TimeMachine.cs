using System.Text.Json;
using Microsoft.Win32;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public partial class WindowsRegistryService
{
    private readonly object _timeMachineGate = new();
    private readonly AsyncLocal<TimeMachineState[]?> _trackedValues = new();

    /// <summary>Tracks fixed-name catalogue values only within their owning setting operation.</summary>
    internal IDisposable TrackSetting(SettingDefinition setting, string label)
    {
        var previous = _trackedValues.Value;
        _trackedValues.Value = GetTimeMachineTargets(setting, label).ToArray();
        return new TrackingScope(() => _trackedValues.Value = previous);
    }

    internal static IEnumerable<TimeMachineState> GetTimeMachineTargets(SettingDefinition setting, string label) =>
        setting.RegistrySettings
            .Where(x => x.ValueName is not null && !x.ApplyPerMonitor && !x.ApplyPerNetworkInterface &&
                !x.LockKeyAccess && (x.KeyPath.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase) ||
                    x.KeyPath.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase) ||
                    x.KeyPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ||
                    x.KeyPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)))
            .Select(x => new TimeMachineState(new(TimeMachineArea.Registry, x.KeyPath, x.ValueName),
                label, TimeMachineValue.Missing));

    private TimeMachineState? FindTrackedValue(string key, string name) =>
        _trackedValues.Value?.FirstOrDefault(x => string.Equals(x.Target.Key, key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Target.Name, name, StringComparison.OrdinalIgnoreCase));

    internal TimeMachineValue ReadTimeMachineValue(TimeMachineTarget target)
    {
        lock (_timeMachineGate) return ReadRawTimeMachineValue(target);
    }

    private TimeMachineValue ReadRawTimeMachineValue(TimeMachineTarget target)
    {
        var (root, path) = ParseKeyPath(target.Key);
        using var key = root.OpenSubKey(path);
        var value = key?.GetValue(target.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? TimeMachineValue.Missing : TimeMachineValue.From(value, (int)key!.GetValueKind(target.Name));
    }

    /// <summary>Rechecks the reviewed value, retains the previous raw data and verifies the selected write.</summary>
    internal bool WriteTimeMachineValue(TimeMachineState target, TimeMachineValue desired, TimeMachineValue? expected = null)
    {
        lock (_timeMachineGate)
        {
            var store = timeMachine ?? throw new InvalidOperationException("TimeMachine storage is unavailable.");
            var before = ReadRawTimeMachineValue(target.Target);
            if (expected is not null && !before.SameAs(expected))
                throw new IOException("The registry value changed after review. Compare again.");
            var (root, path) = ParseKeyPath(target.Target.Key);
            var name = target.Target.Name ?? "";
            if (before.SameAs(desired))
                return true;

            var kind = (RegistryValueKind?)desired.RegistryKind;
            var data = desired.Json is null ? null : DecodeRegistryValue(desired.Json, kind);
            var undo = store.PrepareUndo(target with { Value = before }, desired);
            // Keep a verified previous value if a native write or readback ends in an uncertain state.
            try
            {
                var written = data is null ? DeleteRawValue(root, path, name) : SetRawValue(root, path, name, data, kind!.Value);
                if (!written || !ReadRawTimeMachineValue(target.Target).SameAs(desired))
                    throw new IOException("Windows did not confirm the registry change.");
            }
            catch
            {
                if (ReadRawTimeMachineValue(target.Target).SameAs(before)) store.DiscardUndo(undo.Id);
                else store.Record(LogTopic.Settings, target.Label, "Registry write requires review", undo);
                throw;
            }
            store.Record(LogTopic.Settings, target.Label, "Registry value changed", undo);
            return true;
        }
    }

    /// <summary>Normalizes a requested write to the native kind and the value RegistryKey will store.</summary>
    private static TimeMachineValue EncodeRegistryValue(object value, RegistryValueKind kind)
    {
        if (kind == RegistryValueKind.Unknown)
            kind = value switch
            {
                int => RegistryValueKind.DWord,
                byte[] => RegistryValueKind.Binary,
                string[] => RegistryValueKind.MultiString,
                _ => RegistryValueKind.String
            };
        object? raw = kind switch
        {
            RegistryValueKind.DWord => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.String or RegistryValueKind.ExpandString => value.ToString(),
            _ => value
        };
        return TimeMachineValue.From(raw, (int)kind);
    }

    private static object DecodeRegistryValue(string json, RegistryValueKind? kind) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => JsonSerializer.Deserialize<string>(json)!,
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(json)!,
        RegistryValueKind.Binary or RegistryValueKind.None => JsonSerializer.Deserialize<byte[]>(json)!,
        RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(json),
        RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(json),
        _ => throw new IOException("Unsupported registry backup type.")
    };

    private sealed class TrackingScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
