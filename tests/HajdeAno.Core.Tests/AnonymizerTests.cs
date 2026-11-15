using HajdeAno.Core.Llm;

namespace HajdeAno.Core.Tests;

public class AnonymizerTests
{
    private const string DesignExample =
        "Anna Müller wohnt in Zürich. Die Stiftung Beispiel hat ihr eine Wohnung vermittelt. Die Verwaltung verlangt CHF 4500 für die Räumung.";

    private static readonly FakeExtractor DesignExtractor = FakeExtractor.Knowing(
        ("Anna Müller", "PERSON"),
        ("Zürich", "CITY"),
        ("Stiftung Beispiel", "ORGANIZATION"),
        ("CHF 4500", "AMOUNT")
    );

    private static Task<AnalysisResult> Analyze(
        string text,
        IEntityExtractor? extractor,
        CaseDictionary? dict = null,
        AnonymizationOptions? options = null
    ) =>
        new Anonymizer(extractor, options ?? new AnonymizationOptions()).AnalyzeAsync(
            text,
            dict ?? new CaseDictionary(),
            null,
            CancellationToken.None
        );

    [Fact]
    public async Task Design_example()
    {
        var result = await Analyze(DesignExample, DesignExtractor);
        Assert.Equal(
            "[PERSON_1] wohnt in [CITY_1]. Die [ORG_1] hat ihr eine Wohnung vermittelt. Die Verwaltung verlangt [AMOUNT_1] für die Räumung.",
            result.AnonymizedText
        );
    }

    [Fact]
    public async Task Exact_round_trip()
    {
        var dict = new CaseDictionary();
        var result = await Analyze(DesignExample, DesignExtractor, dict);
        var restored = Restorer.Restore(result.AnonymizedText, dict);
        Assert.Equal(DesignExample, restored.Text);
        Assert.Empty(restored.Issues);
    }

    [Fact]
    public async Task Honorifics_stay_visible_and_partial_names_link()
    {
        var text = "Anna Müller schreibt. Frau Müller ist krank. Anna bittet um Aufschub.";
        var extractor = FakeExtractor.Knowing(
            ("Anna Müller", "PERSON"),
            ("Frau Müller", "PERSON"),
            ("Anna", "PERSON")
        );
        var result = await Analyze(text, extractor);
        Assert.Equal(
            "[PERSON_1] schreibt. Frau [PERSON_1_LAST] ist krank. [PERSON_1_FIRST] bittet um Aufschub.",
            result.AnonymizedText
        );
    }

    [Fact]
    public async Task Familie_becomes_family_placeholder()
    {
        var text = "Familie Keller wohnt hier. Tom Keller und Lea Keller sind Geschwister.";
        var extractor = FakeExtractor.Knowing(
            ("Familie Keller", "PERSON"),
            ("Tom Keller", "PERSON"),
            ("Lea Keller", "PERSON")
        );
        var result = await Analyze(text, extractor);
        Assert.Equal(
            "Familie [FAMILY_1] wohnt hier. [PERSON_1] und [PERSON_2] sind Geschwister.",
            result.AnonymizedText
        );
    }

    [Fact]
    public async Task Consistent_placeholders_across_documents()
    {
        var dict = new CaseDictionary();
        var extractor = FakeExtractor.Knowing(
            ("Anna Müller", "PERSON"),
            ("Peter Keller", "PERSON"),
            ("Stiftung Beispiel", "ORGANIZATION")
        );
        var first = await Analyze("Peter Keller und Anna Müller.", extractor, dict);
        Assert.Equal("[PERSON_1] und [PERSON_2].", first.AnonymizedText);

        // Second document: the model misses a name, the dictionary still catches it.
        var forgetful = FakeExtractor.Knowing(("Stiftung Beispiel", "ORGANIZATION"));
        var second = await Analyze(
            "Anna Müller schreibt der Stiftung Beispiel wegen Peter Keller.",
            forgetful,
            dict
        );
        Assert.Equal("[PERSON_2] schreibt der [ORG_1] wegen [PERSON_1].", second.AnonymizedText);
    }

    [Fact]
    public async Task Invalid_json_is_salvaged_and_reported()
    {
        var extractor = new FakeExtractor(_ =>
            "Sure! Here you go: [{\"text\": \"Anna Müller\", \"type\": \"PERSON\"}, {\"text\": \"Züri"
        );
        var result = await Analyze(
            DesignExample,
            extractor,
            options: new AnonymizationOptions { SecondPass = false }
        );
        Assert.Contains(result.Detections, d => d.Text == "Anna Müller");
        Assert.Equal(1, result.FirstPassStats!.InvalidJsonChunks);
        Assert.Contains(result.Warnings, w => w.Contains("invalid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Garbage_output_does_not_crash()
    {
        var extractor = new FakeExtractor(_ => "<<<garbage>>> }{ ][");
        var result = await Analyze(DesignExample, extractor);
        Assert.Contains(result.Detections, d => d.Text == "CHF 4500"); // regex still works
    }

    [Fact]
    public async Task Hallucinated_entities_are_discarded()
    {
        var extractor = FakeExtractor.Knowing(("Anna Müller", "PERSON"));
        var lying = new FakeExtractor(chunk =>
            extractor.ExtractRawAsync(chunk, null, default).Result.TrimEnd(']')
            + ", {\"text\": \"Hans Gerber\", \"type\": \"PERSON\"}]"
        );
        var result = await Analyze(
            DesignExample,
            lying,
            options: new AnonymizationOptions { SecondPass = false }
        );
        Assert.DoesNotContain(result.Detections, d => d.Text == "Hans Gerber");
        Assert.Equal(1, result.FirstPassStats!.HallucinatedEntities);
    }

    [Fact]
    public async Task Case_mismatch_is_located_but_flagged()
    {
        var extractor = FakeExtractor.Knowing(("anna müller", "PERSON"));
        var lower = new FakeExtractor(_ => "[{\"text\": \"anna müller\", \"type\": \"PERSON\"}]");
        var result = await Analyze(
            DesignExample,
            lower,
            options: new AnonymizationOptions { SecondPass = false }
        );
        var d = Assert.Single(result.Detections, d => d.Category == Category.Person);
        Assert.Equal("Anna Müller", d.Text);
        Assert.True(d.NeedsReview);
        _ = extractor;
    }

    [Fact]
    public async Task Second_pass_adds_leftovers_for_review()
    {
        // The first call misses "Keller"; any later call (on anonymized text) finds it.
        var calls = 0;
        var extractor = new FakeExtractor(chunk =>
        {
            calls++;
            return calls == 1 ? "[{\"text\": \"Anna Müller\", \"type\": \"PERSON\"}]"
                : chunk.Contains("Keller", StringComparison.Ordinal)
                    ? "[{\"text\": \"Keller\", \"type\": \"PERSON\"}, {\"text\": \"[PERSON_1]\", \"type\": \"PERSON\"}]"
                : "[]";
        });
        var result = await Analyze("Anna Müller und Herr Keller.", extractor);
        var leftover = Assert.Single(result.Detections, d => d.Text == "Keller");
        Assert.Equal(DetectionSource.SecondPass, leftover.Source);
        Assert.True(leftover.NeedsReview);
        Assert.Equal("[PERSON_1] und Herr [PERSON_2].", result.AnonymizedText);
    }

    [Fact]
    public async Task Long_documents_are_chunked_without_truncation()
    {
        var paragraphs = Enumerable
            .Range(1, 60)
            .Select(i =>
                $"Absatz {i}: Die Person Kim{i:D3} Muster wohnt im Haus Nummer {i}. Weitere Erläuterungen folgen hier."
            );
        var text = string.Join("\n\n", paragraphs);
        var extractor = new FakeExtractor(
            chunk =>
                "["
                + string.Join(
                    ", ",
                    System
                        .Text.RegularExpressions.Regex.Matches(chunk, @"Kim\d{3} Muster")
                        .Select(m => $"{{\"text\": \"{m.Value}\", \"type\": \"PERSON\"}}")
                )
                + "]",
            maxChunkChars: 800
        );
        var result = await Analyze(
            text,
            extractor,
            options: new AnonymizationOptions { SecondPass = false }
        );
        Assert.True(extractor.Calls.Count > 5);
        for (var i = 1; i <= 60; i++)
        {
            Assert.DoesNotContain($"Kim{i:D3}", result.AnonymizedText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Entity_found_in_one_chunk_is_replaced_everywhere()
    {
        var text =
            "Zora Blum ist hier.\n\n"
            + new string('x', 10)
            + string.Concat(Enumerable.Repeat(" Text.", 300))
            + "\n\nZora Blum ist dort.";
        var extractor = new FakeExtractor(
            chunk =>
                chunk.StartsWith("Zora", StringComparison.Ordinal)
                    ? "[{\"text\": \"Zora Blum\", \"type\": \"PERSON\"}]"
                    : "[]",
            maxChunkChars: 600
        );
        var result = await Analyze(
            text,
            extractor,
            options: new AnonymizationOptions { SecondPass = false }
        );
        Assert.DoesNotContain("Zora", result.AnonymizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lenient_mode_keeps_indirect_identifiers()
    {
        var result = await Analyze(
            DesignExample,
            DesignExtractor,
            options: new AnonymizationOptions { StrictMode = false }
        );
        Assert.Contains("Zürich", result.AnonymizedText, StringComparison.Ordinal);
        Assert.Contains("CHF 4500", result.AnonymizedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna", result.AnonymizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_category_is_skipped()
    {
        var options = new AnonymizationOptions
        {
            EnabledCategories = new HashSet<Category>(
                Categories.All.Where(c => c != Category.Amount)
            ),
        };
        var result = await Analyze(DesignExample, DesignExtractor, options: options);
        Assert.Contains("CHF 4500", result.AnonymizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ignored_terms_are_not_detected()
    {
        var dict = new CaseDictionary();
        dict.Ignore("Zürich");
        var result = await Analyze(DesignExample, DesignExtractor, dict);
        Assert.Contains("Zürich", result.AnonymizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_detection_stays_in_clear()
    {
        var result = await Analyze(DesignExample, DesignExtractor);
        result.Detections.Single(d => d.Text == "Zürich").Status = ReviewStatus.Rejected;
        Assert.Contains("wohnt in Zürich", result.AnonymizedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_selection_replaces_all_occurrences()
    {
        var dict = new CaseDictionary();
        var text = "Der Hund Bello bellt. Bello ist brav.";
        var result = await Analyze(text, null, dict);
        var start = text.IndexOf("Bello", StringComparison.Ordinal);
        var detections = Anonymizer.AddManual(
            text,
            result.Detections,
            start,
            5,
            Category.Other,
            dict
        );
        Assert.Equal("Der Hund [ID_1] bellt. [ID_1] ist brav.", Anonymizer.Apply(text, detections));
    }

    [Fact]
    public async Task Fuzzy_match_catches_misspelled_known_name()
    {
        var dict = new CaseDictionary();
        dict.Assign("Zeynep Yildirim", Category.Person);
        var result = await Analyze("Frau Yildirm hat angerufen.", null, dict);
        var d = Assert.Single(result.Detections, d => d.Text == "Yildirm");
        Assert.Equal(DetectionSource.Fuzzy, d.Source);
        Assert.True(d.NeedsReview);
        Assert.Equal("Frau [PERSON_1_LAST] hat angerufen.", result.AnonymizedText);
    }

    [Fact]
    public async Task Abbreviation_in_parentheses_links_to_preceding_org()
    {
        var text = "Die Sozialversicherungsanstalt Bern (SVA) verfügt. Die SVA zahlt nicht.";
        var extractor = FakeExtractor.Knowing(
            ("Sozialversicherungsanstalt Bern", "GOVERNMENT_AGENCY"),
            ("SVA", "ORG_ABBREVIATION")
        );
        var result = await Analyze(text, extractor);
        Assert.Equal(
            "Die [AGENCY_1] ([AGENCY_1_ABBR]) verfügt. Die [AGENCY_1_ABBR] zahlt nicht.",
            result.AnonymizedText
        );
    }

    [Fact]
    public async Task Without_model_there_is_a_warning()
    {
        var result = await Analyze(DesignExample, null);
        Assert.Contains(result.Warnings, w =>
            w.Contains("No local model", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Placeholders_in_input_are_not_double_replaced()
    {
        var result = await Analyze("Siehe [PERSON_1] und CHF 100.", null);
        Assert.Equal("Siehe [PERSON_1] und [AMOUNT_1].", result.AnonymizedText);
    }

    [Fact]
    public async Task Progress_is_monotonic_and_covers_both_passes()
    {
        var reports = new List<AnalysisProgress>();
        var anonymizer = new Anonymizer(DesignExtractor, new AnonymizationOptions());
        await anonymizer.AnalyzeAsync(
            DesignExample,
            new CaseDictionary(),
            new SyncProgress<AnalysisProgress>(reports.Add),
            CancellationToken.None
        );

        var fractions = reports.Select(r => r.Fraction!.Value).ToList();
        for (var i = 1; i < fractions.Count; i++)
        {
            Assert.True(fractions[i] >= fractions[i - 1], $"{fractions[i - 1]} -> {fractions[i]}");
        }

        Assert.Equal(1.0, fractions[^1]);
        Assert.Contains(reports, r =>
            r.Message.StartsWith("Pass 1 of 2", StringComparison.Ordinal)
        );
        Assert.Contains(reports, r =>
            r.Message.StartsWith("Pass 2 of 2", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Token_progress_is_described()
    {
        var reading = Anonymizer.DescribePass(
            0,
            1,
            new ChunkProgress(0, 2, new InferenceProgress(900, 300, 0))
        );
        Assert.Equal(
            "Detecting, part 1 of 2: reading the text: 300 of 900 tokens (33 %)…",
            reading.Message
        );
        var writing = Anonymizer.DescribePass(
            0,
            1,
            new ChunkProgress(0, 2, new InferenceProgress(900, 900, 120))
        );
        Assert.Contains(
            "writing the answer (120 tokens)",
            writing.Message,
            StringComparison.Ordinal
        );
        Assert.True(writing.Fraction > reading.Fraction);
        Assert.True(writing.Fraction < 0.5);
    }
}
