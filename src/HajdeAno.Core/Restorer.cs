using System.Text;
using System.Text.RegularExpressions;

namespace HajdeAno.Core;

/// <summary>Kind of a problem found during restoration.</summary>
public enum RestoreIssueKind
{
    /// <summary>Well-formed placeholder that is not in the case dictionary.</summary>
    Unknown,

    /// <summary>Something that looks like a placeholder but was modified (e.g. lost brackets).</summary>
    Malformed,
}

/// <summary>A problem found during restoration; the text is left unchanged at that spot.</summary>
public sealed record RestoreIssue(RestoreIssueKind Kind, int Start, int Length, string Text);

/// <summary>A placeholder that was restored.</summary>
/// <param name="Start">Start of the value in the restored text.</param>
/// <param name="Length">Length of the value in the restored text.</param>
/// <param name="Placeholder">The placeholder as it appeared in the input.</param>
/// <param name="Value">The original value.</param>
/// <param name="SourceStart">Start of the placeholder in the input text.</param>
public sealed record RestoredSpan(
    int Start,
    int Length,
    string Placeholder,
    string Value,
    int SourceStart
);

/// <summary>Result of a restoration.</summary>
public sealed record RestoreResult(
    string Text,
    IReadOnlyList<RestoredSpan> Restored,
    IReadOnlyList<RestoreIssue> Issues
);

/// <summary>
/// Deterministic restoration of placeholders. Never guesses: unknown or modified
/// placeholders are reported, not replaced.
/// </summary>
public static partial class Restorer
{
    private const string Prefixes =
        "PERSON|FAMILY|ORG|AGENCY|COURT|ADDRESS|CITY|POSTAL_CODE|COUNTRY|NATIONALITY|PHONE|EMAIL|URL|DATE|BIRTHDATE|AMOUNT|IBAN|CASE_ID|CUSTOMER_ID|SSN|PERMIT|ID";

    [GeneratedRegex(
        @"\[(?<token>[A-Z]+(?:_[A-Z]+)*_\d+(?:_(?:FIRST|LAST|SHORT|ABBR))?)\]",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex PlaceholderRegex();

    // Anything resembling a placeholder: optional bracket of any kind, a known prefix in any
    // case, a separator and a number. Exact well-formed placeholders are matched first.
    [GeneratedRegex(
        @"[\[\(\{<«]?\s*\b(?:"
            + Prefixes
            + @")(?:[\s_\-]?(?:FIRST|LAST|SHORT|ABBR))?[\s_\-]*\d+(?:[\s_\-]*(?:FIRST|LAST|SHORT|ABBR))?\b\s*[\]\)\}>»]?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    )]
    private static partial Regex PlaceholderLikeRegex();

    /// <summary>True if the text contains something that looks like a placeholder.</summary>
    public static bool ContainsPlaceholderLike(string text) =>
        PlaceholderRegex().IsMatch(text) || PlaceholderLikeRegex().IsMatch(text);

    /// <summary>Replace all known placeholders by the original values.</summary>
    public static RestoreResult Restore(string text, CaseDictionary dictionary)
    {
        var sb = new StringBuilder(text.Length);
        var restored = new List<RestoredSpan>();
        var issues = new List<RestoreIssue>();
        var covered = new List<(int Start, int End)>();

        var pos = 0;
        foreach (Match m in PlaceholderRegex().Matches(text))
        {
            covered.Add((m.Index, m.Index + m.Length));
            sb.Append(text, pos, m.Index - pos);
            var value = dictionary.Resolve(m.Groups["token"].Value);
            if (value is null)
            {
                issues.Add(new RestoreIssue(RestoreIssueKind.Unknown, m.Index, m.Length, m.Value));
                sb.Append(m.Value);
            }
            else
            {
                restored.Add(new RestoredSpan(sb.Length, value.Length, m.Value, value, m.Index));
                sb.Append(value);
            }

            pos = m.Index + m.Length;
        }

        sb.Append(text, pos, text.Length - pos);

        foreach (Match m in PlaceholderLikeRegex().Matches(text))
        {
            var start = m.Index;
            var end = m.Index + m.Length;
            if (covered.Any(c => start < c.End && c.Start < end))
            {
                continue;
            }

            // Ignore ordinary words that merely start like a prefix, e.g. "Date 2" in English
            // prose is still suspicious, but "Personal" is not matched thanks to \b.
            issues.Add(
                new RestoreIssue(RestoreIssueKind.Malformed, start, m.Length, m.Value.Trim())
            );
        }

        issues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new RestoreResult(sb.ToString(), restored, issues);
    }
}
