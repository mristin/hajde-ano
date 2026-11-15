namespace HajdeAno.App.Controls;

/// <summary>How a piece of text is highlighted.</summary>
public enum SegmentKind
{
    Plain,

    /// <summary>Replaced by a placeholder (approved).</summary>
    Replaced,

    /// <summary>Replaced, but needs a human decision.</summary>
    Review,

    /// <summary>Detected but rejected: stays in clear text.</summary>
    Rejected,

    /// <summary>A placeholder in anonymized text, or a restored value.</summary>
    Placeholder,

    /// <summary>A problem, e.g. an unknown placeholder.</summary>
    Problem,
}

public sealed record TextSegment(string Text, SegmentKind Kind);
