using Microsoft.Win32;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Preserves raw REG_SZ/REG_EXPAND_SZ values and targets the interactive user during OTS elevation.</summary>
public sealed class WindowsEnvironmentVariableStore(IInteractiveUserService user) : IEnvironmentVariableStore
{
    private RegistryKey? Open(EnvironmentScope scope, bool writable)
    {
        if (scope == EnvironmentScope.System)
            return Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable)
                ?? throw new IOException("The system environment registry key is unavailable.");
        if (scope != EnvironmentScope.User) throw new ArgumentOutOfRangeException(nameof(scope));
        if (user.IsOtsElevation)
        {
            if (string.IsNullOrWhiteSpace(user.InteractiveUserSid))
                throw new UtilityOperationException("The interactive user identity is unavailable.");
            return writable ? Registry.Users.CreateSubKey(user.InteractiveUserSid + @"\Environment") :
                Registry.Users.OpenSubKey(user.InteractiveUserSid + @"\Environment");
        }
        return writable ? Registry.CurrentUser.CreateSubKey("Environment") : Registry.CurrentUser.OpenSubKey("Environment");
    }

    public IReadOnlyList<EnvironmentVariableEntry> Read(EnvironmentScope scope)
    {
        using var key = Open(scope, false);
        if (key == null) return [];
        return key.GetValueNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var kind = key.GetValueKind(name);
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                throw new UtilityOperationException($"Environment variable '{name}' has an unsupported registry type: {kind}.");
            return new EnvironmentVariableEntry(name, key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "",
                kind == RegistryValueKind.ExpandString);
        }).ToArray();
    }

    public bool Write(EnvironmentScope scope, EnvironmentVariableEntry? original, EnvironmentVariableEntry? replacement)
    {
        using (var key = Open(scope, true) ?? throw new IOException("The environment registry key could not be opened for editing."))
        {
            if (replacement == null) key.DeleteValue(original!.Name, true);
            else key.SetValue(original?.Name ?? replacement.Name, replacement.Value,
                replacement.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            key.Flush();
        }
        return SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 2000, out _) != IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);
}
