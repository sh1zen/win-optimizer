using WinOptimizer.Core.Features.Common.Constants;
using Microsoft.VisualBasic.FileIO;
using System.Globalization;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Reads Microsoft Handle's file-only CSV and keeps only the selected PID's records.</summary>
internal sealed class WindowsProcessFiles(IProcessExecutor processes, string? temporaryDirectory = null) : IProcessOpenFileSource
{
    private readonly ManagementPowerShellRunner _shell = new(processes, temporaryDirectory);
    public async Task<ProcessFileSnapshot> ReadAsync(ProcessIdentity identity, bool acceptLicense, CancellationToken token)
    {
        var output = await _shell.RunAsync("Process-Handles", new
        {
            AcceptLicense = acceptLicense, identity.Pid,
            ToolDirectory = Path.Combine(AppDataPaths.Tools, "handle")
        }, token, "Open-file query").ConfigureAwait(false);
        try
        {
            var data = JsonSerializer.Deserialize<HandleOutput>(output, new JsonSerializerOptions { RespectRequiredConstructorParameters = true })
                ?? throw new JsonException("Missing Handle output.");
            if (data.Csv == null || data.Diagnostic == null) throw new JsonException("Incomplete Handle output.");
            return new(identity, DateTimeOffset.Now, Parse(data.Csv, identity.Pid), data.Diagnostic);
        }
        catch (JsonException ex) { throw new UtilityOperationException("Microsoft Handle returned an invalid response.", ex); }
    }

    internal static IReadOnlyList<ProcessOpenFile> Parse(string csv, int pid)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF'))) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        try
        {
            var header = parser.ReadFields() ?? throw new FormatException("Missing Handle CSV header.");
            var columns = header.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "Process", "PID", "Type", "Handle", "Name" })
                if (!columns.ContainsKey(name)) throw new FormatException("Missing Handle column: " + name);
            var result = new List<ProcessOpenFile>();
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? throw new FormatException("Missing Handle CSV record.");
                if (fields.Length != header.Length) throw new FormatException("Incomplete Handle CSV record.");
                if (!int.TryParse(fields[columns["PID"]], NumberStyles.None, CultureInfo.InvariantCulture, out var owner)) throw new FormatException("Invalid Handle PID.");
                if (owner != pid) continue;
                if (!fields[columns["Type"]].Equals("File", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new(fields[columns["Handle"]], fields[columns["Type"]], fields[columns["Name"]]));
            }
            return result;
        }
        catch (Exception ex) when (ex is FormatException or MalformedLineException or ArgumentException)
        { throw new UtilityOperationException("Microsoft Handle returned unsupported file-reference output.", ex); }
    }

    private sealed record HandleOutput(string Csv, string Diagnostic);
}
