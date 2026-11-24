using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Reads bounded text excerpts from Office and OpenDocument packages.</summary>
internal static class OfficePreviewText
{
    private const int TextLimit = 8 * 1024 * 1024;
    private const int EntryLimit = 4096;
    private const long ExpandedLimit = 256 * 1024 * 1024;
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Odt = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    private static void CheckPackage(ZipArchive archive)
    {
        if (archive.Entries.Count > EntryLimit) throw new InvalidDataException("The document package contains too many parts.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length < 0 || entry.Length > ExpandedLimit - total)
                throw new InvalidDataException("The document package exceeds the expansion limit.");
            if (!names.Add(entry.FullName)) throw new InvalidDataException("The document package contains duplicate entries.");
            total += entry.Length;
        }
    }

    private static XDocument ReadPart(ZipArchive archive, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Missing document part: {name}");
        if (entry.Length > TextLimit) throw new InvalidDataException("A document part exceeds 8 MiB.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = TextLimit });
        return XDocument.Load(reader);
    }

    internal static (string Text, bool IsPartial) Read(string path, string source, int limit, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        CheckPackage(archive);
        string[] parts = source switch
        {
            "docx" => ["word/document.xml"],
            "odt" => ["content.xml"],
            "pptx" => SlideOrder(archive, ct),
            _ => throw new ArgumentException("Unsupported Office preview format.", nameof(source))
        };
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            var doc = ReadPart(archive, part, ct);
            var paragraphs = source switch
            {
                "docx" => doc.Descendants(Word + "p").Select(p => string.Concat(p.Descendants(Word + "t").Select(t => t.Value))),
                "pptx" => doc.Descendants(Drawing + "p").Select(p => string.Concat(p.Descendants(Drawing + "t").Select(t => t.Value))),
                _ => doc.Descendants().Where(e => e.Name == Odt + "p" || e.Name == Odt + "h").Select(p => p.Value)
            };
            foreach (var paragraph in paragraphs)
            {
                ct.ThrowIfCancellationRequested();
                if (text.Length + paragraph.Length + Environment.NewLine.Length > limit)
                {
                    text.Append(paragraph.AsSpan(0, Math.Min(paragraph.Length, limit - text.Length)));
                    return (text.ToString(), true);
                }
                text.AppendLine(paragraph);
            }
        }
        return (text.ToString(), false);
    }

    private static string[] SlideOrder(ZipArchive archive, CancellationToken ct)
    {
        XNamespace presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XNamespace relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var ids = ReadPart(archive, "ppt/presentation.xml", ct).Descendants(presentation + "sldId");
        var links = ReadPart(archive, "ppt/_rels/presentation.xml.rels", ct).Root!.Elements()
            .Where(e => ((string?)e.Attribute("Type"))?.EndsWith("/slide", StringComparison.Ordinal) == true)
            .ToLookup(e => (string?)e.Attribute("Id"));
        return ids.Select(slide =>
        {
            var target = (string?)links[(string?)slide.Attribute(relationship + "id")].FirstOrDefault()?.Attribute("Target");
            var name = target?.TrimStart('/');
            if (name?.StartsWith("slides/", StringComparison.Ordinal) == true) name = "ppt/" + name;
            if (name?.StartsWith("ppt/slides/", StringComparison.Ordinal) != true || name.Contains(".."))
                throw new InvalidDataException("Invalid slide relationship.");
            return name;
        }).ToArray();
    }
}
