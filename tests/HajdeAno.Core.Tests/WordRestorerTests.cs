using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using HajdeAno.Core.Documents;

namespace HajdeAno.Core.Tests;

public sealed class WordRestorerTests
{
    private const string Ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static CaseDictionary Dictionary()
    {
        var d = new CaseDictionary();
        d.Assign("Zora Blumenthal", Category.Person); // PERSON_1
        d.Assign("Velowerkstatt Speiche GmbH", Category.Organization); // ORG_1
        return d;
    }

    /// <summary>Build a minimal .docx with the given parts (name → body XML).</summary>
    public static byte[] Docx(params (string Name, string Xml)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(
                    zip.CreateEntry(name).Open(),
                    new UTF8Encoding(false)
                );
                w.Write(content);
            }

            Add(
                "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>"""
            );
            foreach (var (name, xml) in parts)
            {
                Add(name, xml);
            }

            var image = zip.CreateEntry("word/media/image1.png");
            using var s = image.Open();
            s.Write([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        }

        return ms.ToArray();
    }

    public static string Body(string inner) =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="{Ns}"><w:body>{inner}</w:body></w:document>""";

    private static (WordRestoreResult Result, Dictionary<string, byte[]> Parts) Restore(byte[] docx)
    {
        using var input = new MemoryStream(docx);
        using var output = new MemoryStream();
        var result = WordRestorer.Restore(input, output, Dictionary());
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        var parts = zip.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var s = e.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                return m.ToArray();
            }
        );
        return (result, parts);
    }

    private static List<string> Paragraphs(byte[] xml) =>
        XDocument
            .Parse(Encoding.UTF8.GetString(xml))
            .Descendants(XName.Get("p", Ns))
            .Select(p => string.Concat(p.Descendants(XName.Get("t", Ns)).Select(t => t.Value)))
            .ToList();

    [Fact]
    public void Placeholders_split_over_runs_are_restored_and_formatting_is_kept()
    {
        var docx = Docx(
            (
                "word/document.xml",
                Body(
                    """
                    <w:p><w:r><w:t xml:space="preserve">Sehr geehrte </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>[PER</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>SON_1]</w:t></w:r><w:r><w:t>,</w:t></w:r></w:p>
                    <w:p><w:r><w:t>Frau [PERSON_1_LAST] bestreitet die Rechnung der [ORG_1].</w:t></w:r></w:p>
                    <w:tbl><w:tr><w:tc><w:p><w:r><w:t>[PERSON_1_FIRST]</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                    """
                )
            )
        );

        var (result, parts) = Restore(docx);
        var paragraphs = Paragraphs(parts["word/document.xml"]);
        Assert.Equal("Sehr geehrte Zora Blumenthal,", paragraphs[0]);
        Assert.Equal(
            "Frau Blumenthal bestreitet die Rechnung der Velowerkstatt Speiche GmbH.",
            paragraphs[1]
        );
        Assert.Equal("Zora", paragraphs[2]);
        Assert.Equal(4, result.Restored);
        Assert.Empty(result.Preview.Issues);

        // The name went into the bold run where the placeholder started.
        var bold = XDocument
            .Parse(Encoding.UTF8.GetString(parts["word/document.xml"]))
            .Descendants(XName.Get("r", Ns))
            .Where(r => r.Descendants(XName.Get("b", Ns)).Any())
            .Select(r => r.Value)
            .ToList();
        Assert.Equal(["Zora Blumenthal", ""], bold);

        // Other parts are copied unchanged.
        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 },
            parts["word/media/image1.png"]
        );
    }

    [Fact]
    public void Headers_footers_notes_and_title_are_restored()
    {
        var part = (string root, string inner) =>
            $"""<?xml version="1.0" encoding="UTF-8"?><w:{root} xmlns:w="{Ns}">{inner}</w:{root}>""";
        var docx = Docx(
            ("word/document.xml", Body("<w:p><w:r><w:t>Inhalt</w:t></w:r></w:p>")),
            ("word/header1.xml", part("hdr", "<w:p><w:r><w:t>Fall [PERSON_1]</w:t></w:r></w:p>")),
            ("word/footer1.xml", part("ftr", "<w:p><w:r><w:t>[ORG_1]</w:t></w:r></w:p>")),
            (
                "word/footnotes.xml",
                part(
                    "footnotes",
                    "<w:footnote><w:p><w:r><w:t>Siehe [PERSON_1_LAST].</w:t></w:r></w:p></w:footnote>"
                )
            ),
            (
                "docProps/core.xml",
                """<?xml version="1.0" encoding="UTF-8"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Antwort an [PERSON_1]</dc:title></cp:coreProperties>"""
            )
        );

        var (result, parts) = Restore(docx);
        Assert.Equal(["Fall Zora Blumenthal"], Paragraphs(parts["word/header1.xml"]));
        Assert.Equal(["Velowerkstatt Speiche GmbH"], Paragraphs(parts["word/footer1.xml"]));
        Assert.Equal(["Siehe Blumenthal."], Paragraphs(parts["word/footnotes.xml"]));
        Assert.Contains(
            "Antwort an Zora Blumenthal",
            Encoding.UTF8.GetString(parts["docProps/core.xml"]),
            StringComparison.Ordinal
        );
        Assert.Equal(4, result.Restored);
        Assert.StartsWith("Inhalt\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_and_modified_placeholders_are_reported_and_left_unchanged()
    {
        var docx = Docx(
            (
                "word/document.xml",
                Body(
                    "<w:p><w:r><w:t>[PERSON_7] schreibt an PERSON 1 und [PERSON_1].</w:t></w:r></w:p>"
                )
            )
        );

        var (result, parts) = Restore(docx);
        Assert.Equal(
            ["[PERSON_7] schreibt an PERSON 1 und Zora Blumenthal."],
            Paragraphs(parts["word/document.xml"])
        );
        Assert.Equal(1, result.Restored);
        Assert.Equal(
            [RestoreIssueKind.Unknown, RestoreIssueKind.Malformed],
            result.Preview.Issues.Select(i => i.Kind)
        );
    }

    [Fact]
    public void Files_that_are_not_word_documents_are_rejected()
    {
        using var output = new MemoryStream();
        Assert.ThrowsAny<InvalidDataException>(() =>
            WordRestorer.Restore(new MemoryStream("not a zip"u8.ToArray()), output, Dictionary())
        );
        Assert.Throws<InvalidDataException>(() =>
            WordRestorer.Restore(
                new MemoryStream(Docx(("word/other.xml", "<x/>"))),
                new MemoryStream(),
                Dictionary()
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            WordRestorer.Restore(
                new MemoryStream(
                    Docx(
                        (
                            "word/document.xml",
                            """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaa">]><x>&a;</x>"""
                        )
                    )
                ),
                new MemoryStream(),
                Dictionary()
            )
        );
    }
}
