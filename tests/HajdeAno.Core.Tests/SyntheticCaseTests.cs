namespace HajdeAno.Core.Tests;

public class SyntheticCaseTests
{
    private static readonly string DataDir = Path.Combine(
        AppContext.BaseDirectory,
        "data",
        "cases"
    );

    public static TheoryData<string> CaseFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(DataDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(f));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public void Annotations_occur_in_documents(string file)
    {
        var c = SyntheticCase.FromJson(File.ReadAllText(Path.Combine(DataDir, file)));
        Assert.NotEmpty(c.Documents);
        foreach (var e in c.Expected)
        {
            Assert.Contains(c.Documents, d => d.Contains(e.Text, StringComparison.Ordinal));
            _ = Categories.FromLlmLabel(e.Type);
        }
    }

    /// <summary>
    /// With a perfect detector (the annotations themselves), every synthetic case must be
    /// fully anonymized and restored exactly. This exercises merging, placeholders and
    /// restoration on realistic data.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseFiles))]
    public async Task Oracle_detector_gives_full_recall_and_exact_round_trip(string file)
    {
        var c = SyntheticCase.FromJson(File.ReadAllText(Path.Combine(DataDir, file)));
        var oracle = FakeExtractor.Knowing(c.Expected.Select(e => (e.Text, e.Type)).ToArray());
        var dict = new CaseDictionary();
        var anonymizer = new Anonymizer(oracle, new AnonymizationOptions());
        foreach (var doc in c.Documents)
        {
            var result = await anonymizer.AnalyzeAsync(doc, dict, null, CancellationToken.None);
            foreach (var e in c.Expected)
            {
                Assert.True(
                    Evaluation.IsCovered(doc, e.Text, result.Detections),
                    $"{e.Type} \"{e.Text}\" not covered"
                );
            }

            var restored = Restorer.Restore(result.AnonymizedText, dict);
            Assert.Empty(restored.Issues);

            // Aliases restore to their canonical form; everything else must be exact.
            var canonical = result.Detections.All(d =>
                dict.Resolve(d.Placeholder!.Trim('[', ']')) == TextUtil.NormalizeWhitespace(d.Text)
            );
            if (canonical)
            {
                Assert.Equal(doc, restored.Text);
            }
        }
    }

    [Fact]
    public async Task Patterns_alone_catch_structured_identifiers()
    {
        var cases = SyntheticCase.LoadAll(DataDir);
        var report = await Evaluation.RunAsync(cases, null, new AnonymizationOptions(), "patterns");
        Assert.Contains(
            "| EMAIL | 100.0 %",
            report.Replace("100.0%", "100.0 %", StringComparison.Ordinal),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "| IBAN | 100.0 %",
            report.Replace("100.0%", "100.0 %", StringComparison.Ordinal),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "| PHONE | 100.0 %",
            report.Replace("100.0%", "100.0 %", StringComparison.Ordinal),
            StringComparison.Ordinal
        );
    }
}
