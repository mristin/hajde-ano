namespace HajdeAno.Core.Llm;

/// <summary>
/// Progress of a single model call: first the prompt is read (<see cref="PromptProcessed"/>
/// grows to <see cref="PromptTokens"/>; a cached prefix counts as processed), then the
/// answer is written token by token.
/// </summary>
public sealed record InferenceProgress(int PromptTokens, int PromptProcessed, int GeneratedTokens);

/// <summary>Progress of a detection run over the chunks of a document.</summary>
public sealed record ChunkProgress(int Chunk, int Chunks, InferenceProgress? Inference);

/// <summary>Abstraction over the local LLM, so that the pipeline is testable without a model.</summary>
public interface IEntityExtractor
{
    /// <summary>Approximate maximum number of characters of input text per call.</summary>
    int MaxChunkChars { get; }

    /// <summary>Return the raw model output for the given text.</summary>
    Task<string> ExtractRawAsync(
        string text,
        IProgress<InferenceProgress>? progress,
        CancellationToken cancellationToken
    );
}

/// <summary>Reports progress synchronously on the calling thread (keeps reports in order).</summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>Statistics of an LLM detection run (no private content).</summary>
public sealed record LlmRunStats(
    int Chunks,
    int InvalidJsonChunks,
    int ReportedEntities,
    int HallucinatedEntities,
    TimeSpan Elapsed
);

/// <summary>Runs the LLM over (chunks of) a document and validates its answers.</summary>
public sealed class LlmDetector(IEntityExtractor extractor)
{
    /// <summary>
    /// Detect entities with the LLM. Every reported span is located in the original text;
    /// spans that do not occur in the text are discarded as hallucinations.
    /// </summary>
    public async Task<(List<Detection> Detections, LlmRunStats Stats)> DetectAsync(
        string text,
        DetectionSource source,
        IProgress<ChunkProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var chunks = TextChunker.Split(
            text,
            extractor.MaxChunkChars,
            Math.Min(400, extractor.MaxChunkChars / 8)
        );

        var raw = new List<RawEntity>();
        var invalid = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = i;
            progress?.Report(new ChunkProgress(chunk, chunks.Count, null));
            var output = await extractor
                .ExtractRawAsync(
                    chunks[i].Text,
                    progress is null
                        ? null
                        : new SyncProgress<InferenceProgress>(p =>
                            progress.Report(new ChunkProgress(chunk, chunks.Count, p))
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var parsed = LlmResponseParser.Parse(output);
            if (!parsed.WasValidJson)
            {
                invalid++;
            }

            raw.AddRange(parsed.Entities);
        }

        var (detections, hallucinated) = Locate(text, raw, source);
        return (
            detections,
            new LlmRunStats(chunks.Count, invalid, raw.Count, hallucinated, started.Elapsed)
        );
    }

    /// <summary>
    /// Find all occurrences of the reported entities in the text (entities are reconciled
    /// across chunks by their text, so a name found in one chunk is replaced everywhere).
    /// </summary>
    public static (List<Detection> Detections, int Hallucinated) Locate(
        string text,
        IEnumerable<RawEntity> entities,
        DetectionSource source
    )
    {
        var detections = new List<Detection>();
        var hallucinated = 0;
        foreach (var group in entities.GroupBy(e => TextUtil.NormalizeWhitespace(e.Text)))
        {
            var needle = group.Key;
            if (needle.Length == 0)
            {
                continue;
            }

            var category = group.First().Category;
            var occurrences = TextUtil.FindOccurrences(text, needle, ignoreCase: false).ToList();
            var needsReview = false;
            if (occurrences.Count == 0)
            {
                // Same letters, different case (e.g. sentence start): still a real span.
                occurrences = TextUtil.FindOccurrences(text, needle, ignoreCase: true).ToList();
                needsReview = occurrences.Count > 0;
            }

            if (occurrences.Count == 0)
            {
                hallucinated++;
                continue;
            }

            foreach (var (start, length) in occurrences)
            {
                detections.Add(
                    new Detection(start, text.Substring(start, length), category, source)
                    {
                        NeedsReview = needsReview || source == DetectionSource.SecondPass,
                        Note =
                            source == DetectionSource.SecondPass
                                ? "Found by the second pass over the anonymized text"
                                : null,
                    }
                );
            }
        }

        return (detections, hallucinated);
    }
}
