using HajdeAno.Core;
using HajdeAno.Core.Llm;

namespace HajdeAno.Llm.Tests;

/// <summary>
/// Integration tests with a 1 MB toy model (stories260K from ggml-org). The model output is
/// nonsense, but the tests exercise the real in-process inference path: native library
/// loading, tokenization, grammar-constrained sampling, parsing and validation.
/// </summary>
public sealed class ToyModelTests : IAsyncLifetime
{
    private static readonly string ModelPath = Path.Combine(
        AppContext.BaseDirectory,
        "data",
        "stories260K.gguf"
    );

    private LlamaEntityExtractor? _extractor;

    public async Task InitializeAsync()
    {
        // Git LFS pointer files are tiny text files: fail with a helpful message.
        Assert.True(
            new FileInfo(ModelPath).Length > 100_000,
            "The toy model is missing; run `git lfs pull`."
        );
        _extractor = await LlamaEntityExtractor.LoadAsync(
            new LlamaOptions
            {
                ModelPath = ModelPath,
                ContextSize = 2048,
                UseChatTemplate = false,
                MaxOutputTokens = 96,
            }
        );
    }

    public Task DisposeAsync()
    {
        _extractor?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Output_is_constrained_by_the_grammar()
    {
        var raw = await _extractor!.ExtractRawAsync(
            "Anna Müller wohnt in Zürich.",
            null,
            CancellationToken.None
        );
        Assert.StartsWith("[", raw.TrimStart(), StringComparison.Ordinal);

        // Valid or truncated: the parser must cope either way.
        _ = LlmResponseParser.Parse(raw);
    }

    [Fact]
    public async Task Pipeline_runs_end_to_end_and_discards_hallucinations()
    {
        const string text =
            "Anna Müller wohnt an der Bahnhofstrasse 12, 8001 Zürich. Tel. 079 123 45 67.";
        var anonymizer = new Anonymizer(_extractor, new AnonymizationOptions());
        var result = await anonymizer.AnalyzeAsync(
            text,
            new CaseDictionary(),
            null,
            CancellationToken.None
        );

        // Whatever the toy model says, every detection must be a real span of the text.
        Assert.All(result.Detections, d => Assert.Equal(d.Text, text.Substring(d.Start, d.Length)));

        // The deterministic detectors still work.
        Assert.DoesNotContain("079 123 45 67", result.AnonymizedText, StringComparison.Ordinal);
        Assert.NotNull(result.FirstPassStats);
    }

    [Fact]
    public async Task Missing_model_is_a_local_error()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            LlamaEntityExtractor.LoadAsync(
                new LlamaOptions { ModelPath = Path.Combine(AppContext.BaseDirectory, "nope.gguf") }
            )
        );
    }
}
