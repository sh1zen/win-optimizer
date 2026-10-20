using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Core.Features.Common.Constants;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Owns authenticated embedded scripts, request encoding and temporary-file lifetimes.</summary>
internal sealed class ManagementPowerShellRunner(IProcessExecutor processes, string? temporaryDirectory = null)
{
    /// <summary>Uses an exclusive temporary script file to avoid the Windows command-line length limit.</summary>
    public async Task<string> RunAsync(string resource, object request, CancellationToken token, string operation = "Autorun operation")
    {
        var directory = temporaryDirectory ?? AppDataPaths.Temp;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "win-optimizer-autoruns-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            byte[] bytes;
            await using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinOptimizer.Autoruns." + resource + ".ps1")
                ?? throw new InvalidOperationException("The embedded startup tool is missing."))
            {
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, token).ConfigureAwait(false); bytes = buffer.ToArray();
            }
            if (resource is "Startup" or "Scan-Autoruns" or "Process-Handles")
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (!text.StartsWith("param(", StringComparison.Ordinal) || text.IndexOf('\n') < 0)
                    throw new InvalidOperationException("Invalid embedded tool parameter contract.");
                var helpers = AppStorageScript.Helpers + "\n";
                if (resource is "Scan-Autoruns" or "Process-Handles")
                {
                    await using var shared = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinOptimizer.Autoruns.Sysinternals-Cache.ps1")
                        ?? throw new InvalidOperationException("The Sysinternals cache implementation is missing.");
                    using var reader = new StreamReader(shared, Encoding.UTF8);
                    helpers += await reader.ReadToEndAsync(token).ConfigureAwait(false) + "\n";
                }
                text = text.Insert(text.IndexOf('\n') + 1, helpers);
                bytes = Encoding.UTF8.GetBytes(text);
            }
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
            }
            var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
            var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            var hash = Convert.ToBase64String(SHA256.HashData(bytes));
            // Authenticate the bytes read once from disk before creating the script block. A replaced
            // temporary file cannot become executable code in an elevated process.
            var loader = $"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}'));" +
                "$b=[IO.File]::ReadAllBytes($p);$s=[Security.Cryptography.SHA256]::Create();" +
                $"try{{$h=[Convert]::ToBase64String($s.ComputeHash($b))}}finally{{$s.Dispose()}};if($h -cne '{hash}'){{throw 'Startup script integrity check failed.'}};" +
                $"& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($b))) -RequestBase64 '{data}'";
            var encodedLoader = Convert.ToBase64String(Encoding.Unicode.GetBytes(loader));
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            token.ThrowIfCancellationRequested();
            // Wait for the complete read/write unit: killing a PowerShell parent can orphan its native child.
            var result = await processes.ExecuteAsync(executable,
                $"-NoProfile -NonInteractive -EncodedCommand {encodedLoader}", CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!result.Succeeded)
                throw new UtilityOperationException(operation + " failed: " + result.StandardError.Trim());
            return result.StandardOutput.Trim().TrimStart('\uFEFF');
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

}
