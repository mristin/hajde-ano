using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HajdeAno.Core.Documents;

/// <summary>Result of restoring a Word document.</summary>
/// <param name="Text">
/// The text of the document with placeholders, one line per paragraph: the body, then
/// headers, footers, notes and comments.
/// </param>
/// <param name="Preview">Restoration of <paramref name="Text"/>, for display and review.</param>
/// <param name="Restored">Number of placeholders restored in the document.</param>
public sealed record WordRestoreResult(string Text, RestoreResult Preview, int Restored);

/// <summary>
/// Restore placeholders in a Word document (.docx), keeping its formatting.
/// </summary>
/// <remarks>
/// A .docx file is a zip archive of XML parts. The text of a paragraph is spread over runs
/// (<c>w:r</c>) with text elements (<c>w:t</c>); Word often splits a placeholder over
/// several runs (because of formatting, spell checking or editing). Placeholders are
/// therefore matched on the text of the whole paragraph, and the value is written into the
/// run where the placeholder starts. The body, headers, footers, footnotes, endnotes and
/// comments are restored, as well as the document title and subject. Everything else is
/// copied unchanged. Like <see cref="Restorer"/>, unknown and modified placeholders are
/// reported and left as they are.
/// </remarks>
public static partial class WordRestorer
{
    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    /// <summary>Upper bound for an XML part, against zip bombs.</summary>
    private const long MaxPartSize = 64L * 1024 * 1024;

    [GeneratedRegex(
        @"^word/(document|header\d*|footer\d*|footnotes|endnotes|comments)\.xml$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex TextPartRegex();

    private const string CorePropertiesPart = "docProps/core.xml";

    /// <summary>Restore the document and write the result to <paramref name="output"/>.</summary>
    /// <exception cref="InvalidDataException">The input is not a valid .docx file.</exception>
    public static WordRestoreResult Restore(Stream input, Stream output, CaseDictionary dictionary)
    {
        var parts = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var restored = 0;
        try
        {
            using var source = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (source.GetEntry("word/document.xml") is null)
            {
                throw new InvalidDataException(
                    "Not a Word document (word/document.xml is missing)"
                );
            }

            using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var entry in source.Entries)
            {
                var isText = TextPartRegex().IsMatch(entry.FullName);
                var isCore = entry.FullName == CorePropertiesPart;
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                using var from = entry.Open();
                using var to = copy.Open();
                if (!isText && !isCore)
                {
                    from.CopyTo(to);
                    continue;
                }

                if (entry.Length > MaxPartSize)
                {
                    throw new InvalidDataException("A part of the document is too large");
                }

                var xml = LoadXml(from);
                if (isText)
                {
                    var paragraphs = new List<string>();
                    restored += RestoreParagraphs(xml, dictionary, paragraphs);
                    parts[entry.FullName] = paragraphs;
                }
                else
                {
                    restored += RestoreCoreProperties(xml, dictionary);
                }

                SaveXml(xml, to);
            }
        }
        catch (Exception e) when (e is XmlException)
        {
            throw new InvalidDataException("The Word document is damaged", e);
        }

        // The body first, then headers, footers, notes and comments.
        var text = string.Join(
            "\n",
            parts.OrderBy(p => p.Key == "word/document.xml" ? 0 : 1).SelectMany(p => p.Value)
        );
        return new WordRestoreResult(text, Restorer.Restore(text, dictionary), restored);
    }

    private static XDocument LoadXml(Stream stream)
    {
        // No DTDs (entity expansion) and no external resources.
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
        );
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void SaveXml(XDocument xml, Stream stream)
    {
        using var writer = XmlWriter.Create(
            stream,
            new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }
        );
        xml.Save(writer);
    }

    /// <summary>Restore every paragraph of a part; collect the body text for the preview.</summary>
    private static int RestoreParagraphs(
        XDocument xml,
        CaseDictionary dictionary,
        List<string> paragraphs
    )
    {
        var restored = 0;
        foreach (var paragraph in xml.Descendants(W + "p").ToList())
        {
            // Text of this paragraph only (text boxes contain paragraphs of their own).
            var texts = paragraph
                .Descendants(W + "t")
                .Where(t => t.Ancestors(W + "p").First() == paragraph)
                .ToList();
            var text = string.Concat(texts.Select(t => t.Value));
            paragraphs.Add(text);

            if (text.Length == 0)
            {
                continue;
            }

            var result = Restorer.Restore(text, dictionary);
            foreach (var span in result.Restored.OrderByDescending(s => s.SourceStart))
            {
                Replace(texts, span.SourceStart, span.Placeholder.Length, span.Value);
                restored++;
            }
        }

        return restored;
    }

    /// <summary>
    /// Replace <paramref name="length"/> characters starting at <paramref name="start"/> of
    /// the concatenated text of the elements: the value goes where the placeholder starts
    /// (keeping that run's formatting), the rest of the placeholder is removed.
    /// </summary>
    private static void Replace(List<XElement> texts, int start, int length, string value)
    {
        var offset = 0;
        var end = start + length;
        var inserted = false;
        foreach (var t in texts)
        {
            var nodeStart = offset;
            var nodeEnd = offset + t.Value.Length;
            offset = nodeEnd;
            if (nodeEnd <= start || nodeStart >= end)
            {
                continue;
            }

            var from = Math.Max(start, nodeStart) - nodeStart;
            var to = Math.Min(end, nodeEnd) - nodeStart;
            var current = t.Value;
            t.Value = current[..from] + (inserted ? "" : value) + current[to..];
            inserted = true;

            // Keep leading and trailing spaces of the new text.
            t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }
    }

    /// <summary>Restore the title, subject, keywords and description of the document.</summary>
    private static int RestoreCoreProperties(XDocument xml, CaseDictionary dictionary)
    {
        var restored = 0;
        foreach (var element in xml.Root?.Elements().Where(e => !e.HasElements) ?? [])
        {
            if (element.Name.Namespace != Dc && element.Name.LocalName != "keywords")
            {
                continue;
            }

            var result = Restorer.Restore(element.Value, dictionary);
            if (result.Restored.Count > 0)
            {
                element.Value = result.Text;
                restored += result.Restored.Count;
            }
        }

        return restored;
    }
}
