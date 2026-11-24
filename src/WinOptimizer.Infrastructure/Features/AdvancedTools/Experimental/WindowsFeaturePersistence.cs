using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Microsoft.Win32;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;

namespace WinOptimizer.Infrastructure.Features.AdvancedTools.Experimental;

/// <summary>Reads and writes only the selected feature's User-priority persistent registry key.</summary>
internal static class WindowsFeaturePersistence
{
    internal const string UserOverrides = @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\8";

    private static readonly string[] Fields =
        ["EnabledState", "EnabledStateOptions", "Variant", "VariantPayloadKind", "VariantPayload"];

    // Windows' registry key encoding, checked against ViVe v0.3.4 and the published CFR key mappings.
    internal static string KeyName(uint id)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        uint encoded = BinaryPrimitives.ReverseEndianness(id ^ 0x74161a4eu);
        return (BitOperations.RotateLeft(encoded ^ 0x8fb23d4fu, 1) ^ 0x833ea8ffu)
            .ToString(CultureInfo.InvariantCulture);
    }

    public static FeatureOverride? Read(RegistryKey machine, uint id)
    {
        using var key = machine.OpenSubKey($@"{UserOverrides}\{KeyName(id)}");
        if (key is null) return null;
        var names = key.GetValueNames();
        if (key.SubKeyCount != 0 || names.Except(Fields, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException($"Feature {id} has unrecognized persistent override data.");

        uint? ReadField(string name)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            if (key.GetValueKind(name) != RegistryValueKind.DWord || key.GetValue(name) is not int value)
                throw new InvalidDataException($"Feature {id} has an invalid {name} value.");
            return unchecked((uint)value);
        }

        var value = new FeatureOverride((FeatureOverrideState?)ReadField(Fields[0]), ReadField(Fields[1]),
            ReadField(Fields[2]), ReadField(Fields[3]), ReadField(Fields[4]));
        return value.IsValid ? value : throw new InvalidDataException($"Feature {id} uses unsupported override fields.");
    }

    public static void Write(RegistryKey machine, uint id, FeatureOverride? before, FeatureOverride? value)
    {
        if (value is { IsValid: false }) throw new ArgumentException("Invalid feature override.", nameof(value));
        if (Read(machine, id) != before)
            throw new IOException($"Feature {id} persistence changed after it was read.");

        var name = KeyName(id);
        if (value is null)
        {
            using var parent = machine.OpenSubKey(UserOverrides, writable: true);
            parent?.DeleteSubKey(name, throwOnMissingSubKey: false);
            parent?.Flush();
        }
        else
        {
            using var key = machine.CreateSubKey($@"{UserOverrides}\{name}");
            uint?[] values = [(uint?)value.State, value.Options, value.Variant, value.PayloadKind, value.Payload];
            for (int i = 0; i < Fields.Length; i++)
            {
                if (values[i] is uint field) key.SetValue(Fields[i], unchecked((int)field), RegistryValueKind.DWord);
                else key.DeleteValue(Fields[i], throwOnMissingValue: false);
            }
            key.Flush();
        }

        if (Read(machine, id) != value)
            throw new IOException($"Feature {id} persistent override could not be verified.");
    }
}
