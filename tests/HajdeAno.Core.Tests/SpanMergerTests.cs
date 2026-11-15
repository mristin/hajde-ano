namespace HajdeAno.Core.Tests;

public class SpanMergerTests
{
    private const string Text = "Anna Müller wohnt an der Bahnhofstrasse 12, 8001 Zürich.";

    private static Detection D(string text, Category c, DetectionSource s, int occurrence = 0)
    {
        var start = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            start = Text.IndexOf(text, start + 1, StringComparison.Ordinal);
        }

        return new Detection(start, text, c, s);
    }

    [Fact]
    public void Longer_span_wins()
    {
        var merged = SpanMerger.Merge(
            Text,
            [
                D("Müller", Category.Person, DetectionSource.Dictionary),
                D("Anna Müller", Category.Person, DetectionSource.Llm),
            ]
        );
        Assert.Single(merged);
        Assert.Equal("Anna Müller", merged[0].Text);
    }

    [Fact]
    public void Same_span_prefers_llm_over_regex()
    {
        var merged = SpanMerger.Merge(
            Text,
            [
                D("Zürich", Category.City, DetectionSource.Regex),
                D("Zürich", Category.Other, DetectionSource.Llm),
            ]
        );
        Assert.Single(merged);
        Assert.Equal(DetectionSource.Llm, merged[0].Source);
    }

    [Fact]
    public void Partial_overlap_is_extended_to_the_union()
    {
        var merged = SpanMerger.Merge(
            Text,
            [
                D("Bahnhofstrasse 12, 8001", Category.Address, DetectionSource.Llm),
                D("8001 Zürich", Category.City, DetectionSource.Regex),
            ]
        );
        Assert.Single(merged);
        Assert.Equal("Bahnhofstrasse 12, 8001 Zürich", merged[0].Text);
        Assert.True(merged[0].NeedsReview);
    }

    [Fact]
    public void Manual_overrides()
    {
        var merged = SpanMerger.Merge(
            Text,
            [
                D("Bahnhofstrasse 12, 8001 Zürich", Category.Address, DetectionSource.Llm),
                D("Zürich", Category.City, DetectionSource.Manual),
            ]
        );
        Assert.Single(merged);
        Assert.Equal(DetectionSource.Manual, merged[0].Source);
    }

    [Fact]
    public void Result_is_sorted_and_non_overlapping()
    {
        var merged = SpanMerger.Merge(
            Text,
            [
                D("Zürich", Category.City, DetectionSource.Regex),
                D("Anna", Category.Person, DetectionSource.Llm),
                D("Anna Müller", Category.Person, DetectionSource.Llm),
                D("8001", Category.PostalCode, DetectionSource.Regex),
            ]
        );
        for (var i = 1; i < merged.Count; i++)
        {
            Assert.True(merged[i - 1].End <= merged[i].Start);
        }
    }
}
