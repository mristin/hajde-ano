using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace HajdeAno.App.Controls;

/// <summary>
/// A selectable text block that renders <see cref="TextSegment"/>s with highlighting.
/// The concatenated segment texts are exactly the underlying text, so selection offsets map
/// one-to-one to offsets in the document.
/// </summary>
public sealed class HighlightedTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<TextSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IReadOnlyList<TextSegment>?>(
            nameof(Segments)
        );

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public IReadOnlyList<TextSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static readonly IBrush ReplacedBrush = new SolidColorBrush(
        Color.FromArgb(0x55, 0x2E, 0xA0, 0x43)
    );
    private static readonly IBrush ReviewBrush = new SolidColorBrush(
        Color.FromArgb(0x88, 0xF5, 0xA6, 0x23)
    );
    private static readonly IBrush RejectedBrush = new SolidColorBrush(
        Color.FromArgb(0x33, 0x80, 0x80, 0x80)
    );
    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(
        Color.FromArgb(0x55, 0x3B, 0x82, 0xF6)
    );
    private static readonly IBrush ProblemBrush = new SolidColorBrush(
        Color.FromArgb(0x77, 0xEF, 0x44, 0x44)
    );

    /// <summary>
    /// Keep the selection when the focus moves elsewhere. SelectableTextBlock clears it,
    /// but the user selects text here and then clicks a button (or picks a category),
    /// which takes the focus; the selection must survive until the command runs.
    /// </summary>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        var (start, end) = (SelectionStart, SelectionEnd);
        base.OnLostFocus(e);
        SelectionStart = start;
        SelectionEnd = end;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        foreach (var s in Segments ?? [])
        {
            var run = new Run(s.Text);
            switch (s.Kind)
            {
                case SegmentKind.Replaced:
                    run.Background = ReplacedBrush;
                    break;
                case SegmentKind.Review:
                    run.Background = ReviewBrush;
                    run.FontWeight = FontWeight.SemiBold;
                    break;
                case SegmentKind.Rejected:
                    run.Background = RejectedBrush;
                    run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                    break;
                case SegmentKind.Placeholder:
                    run.Background = PlaceholderBrush;
                    run.FontWeight = FontWeight.SemiBold;
                    break;
                case SegmentKind.Problem:
                    run.Background = ProblemBrush;
                    break;
                default:
                    break;
            }

            inlines.Add(run);
        }

        Inlines = inlines;
    }
}
