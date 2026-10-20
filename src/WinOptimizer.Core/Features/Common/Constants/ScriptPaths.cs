namespace WinOptimizer.Core.Features.Common.Constants;

public static class ScriptPaths
{
    public static readonly string ScriptsDirectory = AppDataPaths.Scripts;

    /// <summary>Literal path for embedding in generated PowerShell scripts.</summary>
    public const string ScriptsDirectoryLiteral = @"$env:ProgramData\win-optimizer\Scripts";

    /// <summary>Literal path for embedding in generated PowerShell scripts.</summary>
    public const string LogsDirectoryLiteral = @"$env:ProgramData\win-optimizer\Logs";

    /// <summary>Literal path for embedding in generated PowerShell scripts.</summary>
    public const string UnattendScriptPath = @"$env:ProgramData\win-optimizer\Unattend\Scripts\Optimizations.ps1";

    /// <summary>Literal path for embedding in generated PowerShell scripts.</summary>
    public const string PowerShellExePath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
}