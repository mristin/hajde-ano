using HajdeAno.Core.Llm;

namespace HajdeAno.Core.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Short_text_is_one_chunk()
    {
        var chunks = TextChunker.Split("Kurz.", 1000, 100);
        Assert.Single(chunks);
    }

    [Theory]
    [InlineData(200, 20)]
    [InlineData(500, 100)]
    [InlineData(1000, 0)]
    public void Chunks_cover_entire_text_with_overlap(int max, int overlap)
    {
        var text = string.Join(" ", Enumerable.Range(0, 2000).Select(i => $"Wort{i}."));
        var chunks = TextChunker.Split(text, max, overlap);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(text.Length, chunks[^1].Start + chunks[^1].Text.Length);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].Text.Length <= max);
            Assert.Equal(text.Substring(chunks[i].Start, chunks[i].Text.Length), chunks[i].Text);
            if (i > 0)
            {
                // No gap between consecutive chunks.
                Assert.True(chunks[i].Start <= chunks[i - 1].Start + chunks[i - 1].Text.Length);
            }
        }
    }

    [Fact]
    public void Handles_text_without_spaces()
    {
        var text = new string('x', 5000);
        var chunks = TextChunker.Split(text, 1000, 100);
        Assert.Equal(text.Length, chunks[^1].Start + chunks[^1].Text.Length);
    }
}
