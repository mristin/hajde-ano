using HajdeAno.Core.Llm;

namespace HajdeAno.Core.Tests;

/// <summary>Scripted stand-in for the local LLM.</summary>
internal sealed class FakeExtractor(Func<string, string> respond, int maxChunkChars = 4000)
    : IEntityExtractor
{
    public List<string> Calls { get; } = [];

    public int MaxChunkChars => maxChunkChars;

    public Task<string> ExtractRawAsync(string text, CancellationToken cancellationToken)
    {
        Calls.Add(text);
        return Task.FromResult(respond(text));
    }

    /// <summary>Report every given entity that occurs in the chunk.</summary>
    public static FakeExtractor Knowing(params (string Text, string Type)[] entities) =>
        new(chunk =>
            "["
            + string.Join(
                ", ",
                entities
                    .Where(e => chunk.Contains(e.Text, StringComparison.Ordinal))
                    .Select(e =>
                        $"{{\"text\": {System.Text.Json.JsonSerializer.Serialize(e.Text)}, \"type\": \"{e.Type}\"}}"
                    )
            )
            + "]"
        );
}
