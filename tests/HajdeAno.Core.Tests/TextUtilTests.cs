namespace HajdeAno.Core.Tests;

public class TextUtilTests
{
    [Fact]
    public void Finds_whole_words_only()
    {
        var text = "Anna und Annabelle und anna";
        var found = TextUtil.FindOccurrences(text, "Anna", ignoreCase: false).ToList();
        Assert.Single(found);
        Assert.Equal((0, 4), found[0]);

        var ci = TextUtil.FindOccurrences(text, "Anna", ignoreCase: true).ToList();
        Assert.Equal(2, ci.Count);
    }

    [Fact]
    public void Whitespace_in_needle_matches_line_breaks()
    {
        var text = "Stiftung\n  Beispiel und Stiftung Beispiel";
        var found = TextUtil.FindOccurrences(text, "Stiftung Beispiel", ignoreCase: false).ToList();
        Assert.Equal(2, found.Count);
        Assert.Equal("Stiftung\n  Beispiel", text.Substring(found[0].Start, found[0].Length));
    }

    [Fact]
    public void Handles_non_word_edges()
    {
        var text = "Betrag: CHF 4'500.– fällig";
        var found = TextUtil.FindOccurrences(text, "CHF 4'500.–", ignoreCase: false).ToList();
        Assert.Single(found);
    }

    [Theory]
    [InlineData("Müller", "Mueller", 2)]
    [InlineData("Yildirim", "Yildirm", 1)]
    [InlineData("Åkesson", "Åkeson", 1)]
    [InlineData("abcd", "abdc", 1)]
    public void Edit_distance(string a, string b, int expected) =>
        Assert.Equal(expected, TextUtil.EditDistance(a, b));

    [Fact]
    public void Strips_diacritics() =>
        Assert.Equal(
            "Muller Akesson Dorđevic",
            TextUtil.StripDiacritics("Müller Åkesson Đorđević").Replace('Đ', 'D')
        );

    [Fact]
    public void Enumerates_words_with_hyphens()
    {
        var words = TextUtil.Words("Herr Müller-Bianchi, 12 Jahre").Select(w => w.Word).ToList();
        Assert.Equal(["Herr", "Müller-Bianchi", "Jahre"], words);
    }
}
