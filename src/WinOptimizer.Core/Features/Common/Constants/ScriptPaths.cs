namespace WinOptimizer.Core.Features.Common.Constants;

public static class ScriptPaths
{
    public static readonly string ScriptsDirectory = AppDataPaths.Scripts;

    /// <summary>Name used to register the Windows package removal task at sign-in.</summary>
    public const string PackageRemovalTaskName = "BloatRemoval";
    public const string PackageRemovalFileName = PackageRemovalTaskName + ".ps1";
    public static readonly string PackageRemovalScript = Path.Combine(ScriptsDirectory, PackageRemovalFileName);

    /// <summary>Literal path for embedding in generated PowerShell scripts.</summary>
    public const string ScriptsDirectoryLiteral = @"$env:ProgramData\win-optimizer\Scripts";

    /// <summary>Literal path for embedding in generated PowerShell scripts.</summary>
    public const string LogsDirectoryLiteral = @"$env:ProgramData\win-optimizer\Logs";

    /// <summary>Literal path for embedding in generated PowerShell scripts.</summary>
    public const string UnattendScriptPath = @"$env:ProgramData\win-optimizer\Unattend\Scripts\Optimizations.ps1";
}
