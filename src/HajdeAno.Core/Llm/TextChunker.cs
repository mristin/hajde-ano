namespace HajdeAno.Core.Llm;

/// <summary>A chunk of the document.</summary>
public sealed record TextChunk(int Start, string Text);

/// <summary>
/// Splits long documents into overlapping chunks so that nothing is silently truncated.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Split <paramref name="text"/> into chunks of at most <paramref name="maxChars"/>
    /// characters, overlapping by about <paramref name="overlapChars"/>. Chunk borders
    /// prefer paragraph, sentence and word boundaries.
    /// </summary>
    public static IReadOnlyList<TextChunk> Split(string text, int maxChars, int overlapChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapChars);
        if (overlapChars >= maxChars / 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlapChars),
                "Overlap must be less than half the chunk size"
            );
        }

        var chunks = new List<TextChunk>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + maxChars);
            if (end < text.Length)
            {
                end = FindBreak(text, start + maxChars / 2, end);
            }

            chunks.Add(new TextChunk(start, text[start..end]));
            if (end >= text.Length)
            {
                break;
            }

            var next = end - overlapChars;
            next = FindWordStart(text, next, end);
            start = Math.Max(next, start + 1);
        }

        return chunks;
    }

    private static int FindBreak(string text, int min, int max)
    {
        foreach (var separator in new[] { "\n\n", "\n", ". ", "; ", ", ", " " })
        {
            var idx = text.LastIndexOf(separator, max - 1, max - min, StringComparison.Ordinal);
            if (idx >= min)
            {
                return idx + separator.Length;
            }
        }

        return max;
    }

    private static int FindWordStart(string text, int pos, int limit)
    {
        var i = Math.Max(0, pos);
        while (i < limit && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        while (i < limit && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i < limit ? i : Math.Max(0, pos);
    }
}
