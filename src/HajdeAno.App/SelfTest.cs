using HajdeAno.Core;
using HajdeAno.Llm;

namespace HajdeAno.App;

/// <summary>
/// Headless self-test of a published build: loads a model with the bundled native
/// libraries and runs the full pipeline on a fixed synthetic sentence. Used by CI to verify
/// that the self-contained executable works; prints no private data.
/// </summary>
internal static class SelfTest
{
    private const string SyntheticText =
        "Anna Müller wohnt an der Bahnhofstrasse 12, 8001 Zürich. Tel. 079 123 45 67.";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: HajdeAno --self-test MODEL.gguf [--no-template]");
            return 2;
        }

        try
        {
            using var extractor = await LlamaEntityExtractor.LoadAsync(
                new LlamaOptions
                {
                    ModelPath = args[0],
                    ContextSize = 2048,
                    MaxOutputTokens = 128,
                    UseChatTemplate = !args.Contains("--no-template"),
                }
            );
            var anonymizer = new Anonymizer(extractor, new AnonymizationOptions());
            var result = await anonymizer.AnalyzeAsync(
                SyntheticText,
                new CaseDictionary(),
                null,
                CancellationToken.None
            );
            Console.WriteLine(
                $"Self-test OK: {result.Detections.Count} detection(s), "
                    + $"LLM chunks: {result.FirstPassStats?.Chunks ?? 0}"
            );
            Console.WriteLine(result.AnonymizedText);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Self-test FAILED: {e}");
            return 1;
        }
    }
}
