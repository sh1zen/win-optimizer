using Microsoft.VisualBasic.FileIO;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Reads Autorunsc's quoted CSV without losing embedded commas, quotes or multiline commands.</summary>
internal static class AutorunCsvReader
{
    public static IReadOnlyList<AutorunEntry> Read(string csv)
    {
        using var reader = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(",");
        try
        {
            var headers = reader.ReadFields() ?? throw new FormatException("Missing CSV header.");
            if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
                throw new FormatException("Duplicate CSV columns.");
            var columns = headers.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            foreach (var required in new[] { "Time", "Entry Location", "Entry", "Enabled", "Category", "Profile", "Description", "Signer", "Company", "Image Path", "Version", "Launch String" })
                if (!columns.ContainsKey(required)) throw new FormatException("Missing column: " + required);
            var entries = new List<AutorunEntry>();
            while (!reader.EndOfData)
            {
                var fields = reader.ReadFields() ?? throw new FormatException("Missing CSV record.");
                if (fields.Length != headers.Length) throw new FormatException("Unexpected CSV field count.");
                string Field(string name) => fields[columns[name]];
                bool? enabled = Field("Enabled").Trim().ToLowerInvariant() switch
                { "enabled" or "yes" or "true" or "1" => true, "disabled" or "no" or "false" or "0" => false, _ => null };
                entries.Add(new("advanced:" + entries.Count, Field("Entry"), Field("Category"), Field("Entry Location"),
                    Field("Launch String"), Field("Image Path"), Field("Company"), Field("Signer"), Field("Profile"), enabled, false,
                    string.Join("\n", Field("Description"), Field("Version"), Field("Time")).Trim()));
            }
            return entries;
        }
        catch (Exception ex) when (ex is MalformedLineException or FormatException or ArgumentException)
        { throw new UtilityOperationException("Autorunsc returned an unsupported or invalid CSV inventory.", ex); }
    }
}
