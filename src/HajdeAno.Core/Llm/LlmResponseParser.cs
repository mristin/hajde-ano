using System.Text.Json;
using System.Text.RegularExpressions;

namespace HajdeAno.Core.Llm;

/// <summary>An entity as reported by the LLM, before validation.</summary>
public sealed record RawEntity(string Text, Category Category);

/// <summary>Result of parsing the LLM output.</summary>
public sealed record ParseResult(IReadOnlyList<RawEntity> Entities, bool WasValidJson);

/// <summary>Tolerant parser of the LLM output.</summary>
public static partial class LlmResponseParser
{
    [GeneratedRegex(
        """\{\s*"text"\s*:\s*"(?<text>(?:[^"\\]|\\.)*)"\s*,\s*"type"\s*:\s*"(?<type>[A-Za-z_ \-]+)"\s*\}|\{\s*"type"\s*:\s*"(?<type>[A-Za-z_ \-]+)"\s*,\s*"text"\s*:\s*"(?<text>(?:[^"\\]|\\.)*)"\s*\}""",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex ItemRegex();

    /// <summary>
    /// Parse the output. Valid JSON is parsed strictly; otherwise complete items are
    /// salvaged (e.g. from output truncated at the token limit or wrapped in prose).
    /// </summary>
    public static ParseResult Parse(string output)
    {
        var trimmed = StripWrapping(output);
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<RawEntity>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (
                        el.ValueKind == JsonValueKind.Object
                        && el.TryGetProperty("text", out var t)
                        && t.ValueKind == JsonValueKind.String
                    )
                    {
                        var type =
                            el.TryGetProperty("type", out var ty)
                            && ty.ValueKind == JsonValueKind.String
                                ? ty.GetString()!
                                : "OTHER";
                        var text = t.GetString()!;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            list.Add(new RawEntity(text, Categories.FromLlmLabel(type)));
                        }
                    }
                }

                return new ParseResult(Dedupe(list), true);
            }
        }
        catch (JsonException)
        {
            // Fall through to salvaging.
        }

        var salvaged = new List<RawEntity>();
        foreach (Match m in ItemRegex().Matches(output))
        {
            string text;
            try
            {
                text = JsonSerializer.Deserialize<string>("\"" + m.Groups["text"].Value + "\"")!;
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                salvaged.Add(new RawEntity(text, Categories.FromLlmLabel(m.Groups["type"].Value)));
            }
        }

        return new ParseResult(Dedupe(salvaged), false);
    }

    private static string StripWrapping(string output)
    {
        var s = output.Trim();

        // Remove a think block if the model emitted one anyway.
        var thinkEnd = s.IndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0)
        {
            s = s[(thinkEnd + "</think>".Length)..].Trim();
        }

        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = s.IndexOf('\n', StringComparison.Ordinal);
            s = firstNewline >= 0 ? s[(firstNewline + 1)..] : s[3..];
            var fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                s = s[..fence];
            }
        }

        var start = s.IndexOf('[', StringComparison.Ordinal);
        var end = s.LastIndexOf(']');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }

    private static List<RawEntity> Dedupe(List<RawEntity> entities) =>
        entities.DistinctBy(e => (e.Text, e.Category)).ToList();
}
