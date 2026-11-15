using System.Globalization;
using System.Text;

namespace HajdeAno.Core;

/// <summary>Helpers for locating and normalizing text spans.</summary>
public static class TextUtil
{
    /// <summary>Collapse whitespace and trim.</summary>
    public static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Key used to compare surface forms: whitespace-normalized and case-folded.</summary>
    public static string Key(string text) =>
        NormalizeWhitespace(text).Normalize(NormalizationForm.FormC).ToLowerInvariant();

    /// <summary>Remove diacritics, e.g. for fuzzy comparison ("Müller" → "Muller").</summary>
    public static string StripDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Find all occurrences of <paramref name="needle"/> in <paramref name="haystack"/>
    /// such that the match is not part of a larger word.
    /// </summary>
    /// <remarks>
    /// Whitespace in the needle matches any run of whitespace in the haystack, so that
    /// line breaks inside a name do not prevent a match.
    /// </remarks>
    public static IEnumerable<(int Start, int Length)> FindOccurrences(
        string haystack,
        string needle,
        bool ignoreCase
    )
    {
        var normalized = NormalizeWhitespace(needle);
        if (normalized.Length == 0)
        {
            yield break;
        }

        var parts = normalized.Split(' ');
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var searchFrom = 0;
        while (searchFrom < haystack.Length)
        {
            var idx = haystack.IndexOf(parts[0], searchFrom, comparison);
            if (idx < 0)
            {
                yield break;
            }

            var end = idx + parts[0].Length;
            var ok = true;
            for (var i = 1; i < parts.Length && ok; i++)
            {
                var ws = end;
                while (ws < haystack.Length && char.IsWhiteSpace(haystack[ws]))
                {
                    ws++;
                }

                if (
                    ws == end
                    || string.Compare(haystack, ws, parts[i], 0, parts[i].Length, comparison) != 0
                )
                {
                    ok = false;
                }
                else
                {
                    end = ws + parts[i].Length;
                }
            }

            if (ok && IsBoundary(haystack, idx, end, normalized))
            {
                yield return (idx, end - idx);
                searchFrom = end;
            }
            else
            {
                searchFrom = idx + 1;
            }
        }
    }

    private static bool IsBoundary(string text, int start, int end, string needle)
    {
        // Only enforce a boundary where the needle itself starts/ends with a word character.
        var leftOk = !IsWordChar(needle[0]) || start == 0 || !IsWordChar(text[start - 1]);
        var rightOk = !IsWordChar(needle[^1]) || end >= text.Length || !IsWordChar(text[end]);
        return leftOk && rightOk;
    }

    /// <summary>Damerau-Levenshtein (optimal string alignment) distance.</summary>
    public static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost
                );
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>Enumerate the words in the text with their offsets.</summary>
    public static IEnumerable<(int Start, string Word)> Words(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (
                i < text.Length
                && (
                    char.IsLetter(text[i])
                    || (
                        (text[i] == '-' || text[i] == '\'' || text[i] == '’')
                        && i + 1 < text.Length
                        && char.IsLetter(text[i + 1])
                    )
                )
            )
            {
                i++;
            }

            yield return (start, text[start..i]);
        }
    }
}
