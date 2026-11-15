namespace HajdeAno.Core;

/// <summary>Where a detection originates from.</summary>
public enum DetectionSource
{
    /// <summary>Selected by the user.</summary>
    Manual,

    /// <summary>Exact match of a known surface form in the case dictionary.</summary>
    Dictionary,

    /// <summary>Contextual detection by the local LLM.</summary>
    Llm,

    /// <summary>Deterministic regular expression.</summary>
    Regex,

    /// <summary>Approximate (fuzzy) match against the case dictionary.</summary>
    Fuzzy,

    /// <summary>Second LLM pass on the already anonymized text.</summary>
    SecondPass,
}

/// <summary>Review status of a detection.</summary>
public enum ReviewStatus
{
    /// <summary>Not yet reviewed; still replaced in the preview (recall first).</summary>
    Pending,

    /// <summary>Approved for replacement.</summary>
    Approved,

    /// <summary>Rejected; the text stays as-is.</summary>
    Rejected,
}

/// <summary>A span of sensitive text in a document.</summary>
public sealed class Detection
{
    public Detection(int start, string text, Category category, DetectionSource source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentException.ThrowIfNullOrEmpty(text);
        Start = start;
        Text = text;
        Category = category;
        Source = source;
    }

    /// <summary>Start offset (UTF-16 code units) in the original text.</summary>
    public int Start { get; }

    /// <summary>Exact text of the span as it appears in the original.</summary>
    public string Text { get; }

    public int Length => Text.Length;

    /// <summary>End offset (exclusive).</summary>
    public int End => Start + Text.Length;

    public Category Category { get; set; }

    public DetectionSource Source { get; }

    /// <summary>Set if the detection is uncertain and has to be reviewed by a human.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>Optional explanation why the detection needs review.</summary>
    public string? Note { get; set; }

    /// <summary>Placeholder token such as <c>[PERSON_1_FIRST]</c>; assigned after resolution.</summary>
    public string? Placeholder { get; set; }

    /// <summary>Id of the dictionary entity the detection maps to, e.g. <c>PERSON_1</c>.</summary>
    public string? EntityId { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    /// <summary>True if the span is replaced in the anonymized output.</summary>
    public bool IsReplaced => Status != ReviewStatus.Rejected && Placeholder is not null;

    public bool Overlaps(Detection other) => Start < other.End && other.Start < End;

    public bool Overlaps(int start, int end) => Start < end && start < End;

    public override string ToString() => $"{Category}@{Start}:{Text} -> {Placeholder}";
}
