using System.Text;
using HajdeAno.Core.Llm;

namespace HajdeAno.Core;

/// <summary>Options of the anonymization pipeline.</summary>
public sealed record AnonymizationOptions
{
    /// <summary>Also replace indirect identifiers (dates, amounts, nationalities, locations).</summary>
    public bool StrictMode { get; init; } = true;

    /// <summary>Categories that are detected at all.</summary>
    public IReadOnlySet<Category> EnabledCategories { get; init; } =
        new HashSet<Category>(Categories.All);

    /// <summary>Run a second LLM pass over the anonymized text.</summary>
    public bool SecondPass { get; init; } = true;

    public bool IsActive(Category category) =>
        EnabledCategories.Contains(category) && (StrictMode || !category.IsIndirect());
}

/// <summary>Result of an analysis.</summary>
public sealed class AnalysisResult
{
    public required string Text { get; init; }

    public required List<Detection> Detections { get; init; }

    /// <summary>Warnings without private content, e.g. about invalid model output.</summary>
    public List<string> Warnings { get; } = [];

    public LlmRunStats? FirstPassStats { get; set; }

    public LlmRunStats? SecondPassStats { get; set; }

    /// <summary>Anonymized text with all non-rejected detections replaced.</summary>
    public string AnonymizedText => Anonymizer.Apply(Text, Detections);
}

/// <summary>Progress of an analysis for display.</summary>
/// <param name="Message">What is happening right now (no private content).</param>
/// <param name="Fraction">Estimated overall progress in [0, 1], or null if unknown.</param>
public sealed record AnalysisProgress(string Message, double? Fraction);

/// <summary>The anonymization pipeline (see DESIGN.md, "Anonymization Pipeline").</summary>
public sealed class Anonymizer(IEntityExtractor? extractor, AnonymizationOptions options)
{
    private static readonly string[] Honorifics =
    [
        "Herr",
        "Herrn",
        "Frau",
        "Hr.",
        "Fr.",
        "Dr.",
        "Prof.",
        "med.",
        "iur.",
        "phil.",
        "lic.",
        "Familie",
        "Fam.",
        "Mr.",
        "Mrs.",
        "Ms.",
        "Madame",
        "Monsieur",
        "Mme",
        "Signora",
        "Signor",
    ];

    public AnonymizationOptions Options => options;

    /// <summary>
    /// Run the full pipeline: regex, LLM, dictionary, merge, placeholders and the second
    /// pass. The dictionary is extended with newly found entities.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        string text,
        CaseDictionary dictionary,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var result = new AnalysisResult { Text = text, Detections = [] };
        var candidates = new List<Detection>();
        var passes = extractor is not null && options.SecondPass ? 2 : 1;

        progress?.Report(new AnalysisProgress("Running pattern detection…", 0));
        candidates.AddRange(RegexDetector.Detect(text));

        if (extractor is not null)
        {
            var llm = new LlmDetector(extractor);
            var (llmDetections, stats) = await llm.DetectAsync(
                    text,
                    DetectionSource.Llm,
                    progress is null
                        ? null
                        : new SyncProgress<ChunkProgress>(p =>
                            progress.Report(DescribePass(0, passes, p))
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            candidates.AddRange(llmDetections);
            result.FirstPassStats = stats;
            AddStatsWarnings(result, stats, "first pass");
        }
        else
        {
            result.Warnings.Add(
                "No local model loaded: only patterns and the case dictionary were used. Names may be missed."
            );
        }

        progress?.Report(
            new AnalysisProgress("Matching the case dictionary…", passes == 2 ? 0.5 : 0.95)
        );
        candidates.AddRange(DictionaryDetections(text, dictionary));
        candidates.AddRange(FuzzyDetections(text, dictionary));

        result.Detections.AddRange(Resolve(text, candidates, dictionary));

        if (extractor is not null && options.SecondPass)
        {
            var anonymized = Apply(text, result.Detections);
            var started = System.Diagnostics.Stopwatch.StartNew();

            // Collect what the model still considers private in the anonymized text and
            // locate those strings in the original, outside of existing detections.
            var (leftovers, chunks, invalid) = await CollectLeftoversAsync(
                    anonymized,
                    progress is null
                        ? null
                        : new SyncProgress<ChunkProgress>(p =>
                            progress.Report(DescribePass(1, passes, p))
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var (located, hallucinated) = LlmDetector.Locate(
                text,
                leftovers,
                DetectionSource.SecondPass
            );
            var stats = new LlmRunStats(
                chunks,
                invalid,
                leftovers.Count,
                hallucinated,
                started.Elapsed
            );
            var fresh = located.Where(d => !result.Detections.Any(e => e.Overlaps(d))).ToList();
            result.SecondPassStats = stats;
            AddStatsWarnings(result, stats, "second pass");
            if (fresh.Count > 0)
            {
                result.Warnings.Add(
                    $"The second pass found {fresh.Count} additional span(s); please review them."
                );
                var merged = Resolve(text, result.Detections.Concat(fresh), dictionary);
                result.Detections.Clear();
                result.Detections.AddRange(merged);
            }
        }

        progress?.Report(new AnalysisProgress("Done.", 1));
        return result;
    }

    /// <summary>Describe the progress of a model pass and estimate the overall fraction.</summary>
    internal static AnalysisProgress DescribePass(int pass, int passes, ChunkProgress p)
    {
        var label =
            passes == 1 ? "Detecting"
            : pass == 0 ? "Pass 1 of 2: detecting"
            : "Pass 2 of 2: checking the anonymized text";
        var chunk = p.Chunks > 1 ? $", part {p.Chunk + 1} of {p.Chunks}" : "";

        // Reading the prompt is measured exactly; the length of the answer is unknown in
        // advance, so writing approaches the rest asymptotically.
        double within;
        string what;
        if (p.Inference is null)
        {
            within = 0;
            what = "starting…";
        }
        else if (p.Inference.GeneratedTokens == 0)
        {
            var read = (double)p.Inference.PromptProcessed / Math.Max(1, p.Inference.PromptTokens);
            within = 0.4 * read;
            what =
                $"reading the text: {p.Inference.PromptProcessed} of {p.Inference.PromptTokens} tokens ({read * 100:F0} %)…";
        }
        else
        {
            within = 0.4 + (0.55 * (1 - Math.Exp(-p.Inference.GeneratedTokens / 150.0)));
            what = $"writing the answer ({p.Inference.GeneratedTokens} tokens)…";
        }

        var fraction = (pass + ((p.Chunk + within) / Math.Max(1, p.Chunks))) / passes;
        return new AnalysisProgress($"{label}{chunk}: {what}", Math.Clamp(fraction, 0, 0.99));
    }

    private async Task<(List<RawEntity> Leftovers, int Chunks, int Invalid)> CollectLeftoversAsync(
        string anonymized,
        SyncProgress<ChunkProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var leftovers = new List<RawEntity>();
        var chunks = TextChunker.Split(
            anonymized,
            extractor!.MaxChunkChars,
            Math.Min(400, extractor.MaxChunkChars / 8)
        );
        var invalid = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var index = i;
            progress?.Report(new ChunkProgress(index, chunks.Count, null));
            var output = await extractor
                .ExtractRawAsync(
                    chunks[i].Text,
                    progress is null
                        ? null
                        : new SyncProgress<InferenceProgress>(p =>
                            progress.Report(new ChunkProgress(index, chunks.Count, p))
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var parsed = LlmResponseParser.Parse(output);
            if (!parsed.WasValidJson)
            {
                invalid++;
            }

            // Placeholders themselves are not leftovers.
            leftovers.AddRange(
                parsed.Entities.Where(e => !Restorer.ContainsPlaceholderLike(e.Text))
            );
        }

        return (leftovers, chunks.Count, invalid);
    }

    private static void AddStatsWarnings(AnalysisResult result, LlmRunStats stats, string pass)
    {
        if (stats.InvalidJsonChunks > 0)
        {
            result.Warnings.Add(
                $"The model returned invalid JSON for {stats.InvalidJsonChunks} of {stats.Chunks} chunk(s) in the {pass}; partial results were salvaged. Review carefully."
            );
        }

        if (stats.HallucinatedEntities > 0)
        {
            result.Warnings.Add(
                $"Discarded {stats.HallucinatedEntities} reported span(s) that do not occur in the text ({pass})."
            );
        }
    }

    /// <summary>Filter, trim, merge and assign placeholders to candidate detections.</summary>
    public List<Detection> Resolve(
        string text,
        IEnumerable<Detection> candidates,
        CaseDictionary dictionary
    )
    {
        var filtered = candidates
            .Select(TrimHonorifics)
            .Where(d => d is not null)
            .Select(d => d!)
            .Where(d => d.Source == DetectionSource.Manual || options.IsActive(d.Category))
            .Where(d => d.Source == DetectionSource.Manual || !dictionary.IsIgnored(d.Text))
            .Where(d => !Restorer.ContainsPlaceholderLike(d.Text))
            .ToList();

        var merged = SpanMerger.Merge(text, filtered);
        AssignPlaceholders(text, merged, dictionary);
        return merged;
    }

    /// <summary>Assign placeholders in document order (so numbering follows reading order).</summary>
    public static void AssignPlaceholders(
        string text,
        IReadOnlyList<Detection> detections,
        CaseDictionary dictionary
    )
    {
        // Full names before partial mentions so that "Anna" after "Anna Müller" links up,
        // even if "Anna" occurs first in the text.
        var order = detections
            .Select((d, i) => (d, i))
            .OrderBy(x => x.d.Text.Contains(' ', StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x.d.Category == Category.OrganizationAbbreviation ? 1 : 0)
            .ThenBy(x => x.i)
            .Select(x => x.d)
            .ToList();

        foreach (var d in order)
        {
            if (d.Placeholder is not null && dictionary.Find(d.EntityId ?? "") is not null)
            {
                continue;
            }

            Entity? hint = null;
            if (d.Category == Category.OrganizationAbbreviation)
            {
                hint = PrecedingOrganization(text, d, detections, dictionary);
            }

            var a = dictionary.Assign(d.Text, d.Category, hint);
            d.Placeholder = a.Placeholder;
            d.EntityId = a.EntityId;
            if (a.NeedsReview)
            {
                d.NeedsReview = true;
                d.Note ??= a.Note;
            }
        }
    }

    /// <summary>Find an organization directly before "(ABBR)".</summary>
    private static Entity? PrecedingOrganization(
        string text,
        Detection abbr,
        IReadOnlyList<Detection> detections,
        CaseDictionary dictionary
    )
    {
        var i = abbr.Start - 1;
        if (i < 0 || text[i] != '(')
        {
            return null;
        }

        i--;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        var org = detections.FirstOrDefault(d =>
            d.End == i + 1
            && d.Category is Category.Organization or Category.GovernmentAgency or Category.Court
        );
        return org?.EntityId is null ? null : dictionary.Find(org.EntityId);
    }

    private static Detection? TrimHonorifics(Detection d)
    {
        if (d.Category is not (Category.Person or Category.Family))
        {
            return d;
        }

        var text = d.Text;
        var start = d.Start;
        var category = d.Category;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var h in Honorifics)
            {
                if (
                    text.StartsWith(h, StringComparison.Ordinal)
                    && text.Length > h.Length
                    && char.IsWhiteSpace(text[h.Length])
                )
                {
                    var skip = h.Length;
                    while (skip < text.Length && char.IsWhiteSpace(text[skip]))
                    {
                        skip++;
                    }

                    if (h is "Familie" or "Fam.")
                    {
                        category = Category.Family;
                    }

                    text = text[skip..];
                    start += skip;
                    changed = true;
                }
            }
        }

        if (text.Length == 0)
        {
            return null;
        }

        if (start == d.Start && category == d.Category)
        {
            return d;
        }

        return new Detection(start, text, category, d.Source)
        {
            NeedsReview = d.NeedsReview,
            Note = d.Note,
        };
    }

    /// <summary>Exact matches of all surface forms of the case dictionary.</summary>
    public static IEnumerable<Detection> DictionaryDetections(
        string text,
        CaseDictionary dictionary
    )
    {
        foreach (var form in dictionary.SurfaceForms().Values)
        {
            var entity = dictionary.Find(form.EntityId);
            if (entity is null)
            {
                continue;
            }

            foreach (
                var (start, length) in TextUtil.FindOccurrences(text, form.Text, ignoreCase: true)
            )
            {
                var exactCase = string.CompareOrdinal(text, start, form.Text, 0, length) == 0;
                yield return new Detection(
                    start,
                    text.Substring(start, length),
                    entity.Category,
                    DetectionSource.Dictionary
                )
                {
                    Placeholder = form.Placeholder,
                    EntityId = form.EntityId,
                    NeedsReview = form.Ambiguous || !exactCase,
                    Note = form.Note ?? (exactCase ? null : "Different capitalization"),
                };
            }
        }
    }

    /// <summary>Words that are similar (spelling mistakes) to known names.</summary>
    public static IEnumerable<Detection> FuzzyDetections(string text, CaseDictionary dictionary)
    {
        var nameParts = new List<(string Part, string Placeholder, Entity Entity)>();
        foreach (var e in dictionary.Entities)
        {
            if (e.Category == Category.Person)
            {
                if (e.FirstName is not null)
                {
                    foreach (var w in e.FirstName.Split(' '))
                    {
                        nameParts.Add((w, $"[{e.Id}_{CaseDictionary.PartFirst}]", e));
                    }
                }

                if (e.LastName is not null && !e.LastName.Contains(' ', StringComparison.Ordinal))
                {
                    nameParts.Add((e.LastName, $"[{e.Id}_{CaseDictionary.PartLast}]", e));
                }

                if (
                    e.FirstName is null
                    && e.LastName is null
                    && !e.Value.Contains(' ', StringComparison.Ordinal)
                )
                {
                    nameParts.Add((e.Value, e.Placeholder, e));
                }
            }
        }

        var known = dictionary.SurfaceForms();
        foreach (var (start, word) in TextUtil.Words(text))
        {
            if (word.Length < 5 || !char.IsUpper(word[0]) || known.ContainsKey(TextUtil.Key(word)))
            {
                continue;
            }

            var a = TextUtil.StripDiacritics(word.ToLowerInvariant());
            foreach (var (part, placeholder, entity) in nameParts)
            {
                if (part.Length < 5)
                {
                    continue;
                }

                var b = TextUtil.StripDiacritics(part.ToLowerInvariant());
                var allowed = Math.Min(a.Length, b.Length) >= 10 ? 2 : 1;
                if (a == b || TextUtil.EditDistance(a, b) <= allowed)
                {
                    yield return new Detection(start, word, entity.Category, DetectionSource.Fuzzy)
                    {
                        Placeholder = placeholder,
                        EntityId = entity.Id,
                        NeedsReview = true,
                        Note = $"Possible misspelling of \"{part}\"",
                    };
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Add a manual detection for the span and for all other occurrences of the same text
    /// that are not yet covered.
    /// </summary>
    public static List<Detection> AddManual(
        string text,
        List<Detection> detections,
        int start,
        int length,
        Category category,
        CaseDictionary dictionary
    )
    {
        var span = text.Substring(start, length);
        var trimmedStart = span.Length - span.TrimStart().Length;
        span = span.Trim();
        if (span.Length == 0)
        {
            return detections;
        }

        start += trimmedStart;
        var manual = new List<Detection>
        {
            new(start, span, category, DetectionSource.Manual) { Status = ReviewStatus.Approved },
        };
        foreach (var (s, l) in TextUtil.FindOccurrences(text, span, ignoreCase: false))
        {
            if (s != start && !detections.Any(d => d.Overlaps(s, s + l)))
            {
                manual.Add(
                    new Detection(s, text.Substring(s, l), category, DetectionSource.Manual)
                    {
                        Status = ReviewStatus.Approved,
                    }
                );
            }
        }

        // Unignore the term if it was rejected earlier.
        dictionary.IgnoredTerms.Remove(TextUtil.Key(span));

        var merged = SpanMerger.Merge(text, detections.Concat(manual));
        AssignPlaceholders(text, merged, dictionary);
        return merged;
    }

    /// <summary>Replace all non-rejected detections with their placeholders.</summary>
    public static string Apply(string text, IEnumerable<Detection> detections)
    {
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (var d in detections.Where(d => d.IsReplaced).OrderBy(d => d.Start))
        {
            if (d.Start < pos)
            {
                continue; // Defensive: overlaps are resolved upstream.
            }

            sb.Append(text, pos, d.Start - pos);
            sb.Append(d.Placeholder);
            pos = d.End;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
