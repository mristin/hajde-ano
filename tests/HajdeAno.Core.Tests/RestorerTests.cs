namespace HajdeAno.Core.Tests;

public class RestorerTests
{
    private static CaseDictionary Dict()
    {
        var d = new CaseDictionary();
        d.Assign("Stiftung Beispiel", Category.Organization);
        d.Assign("CHF 4'500.–", Category.Amount);
        d.Assign("2024-118734", Category.DebtCollectionReference);
        d.Assign("Anna Müller", Category.Person);
        return d;
    }

    [Fact]
    public void Restores_design_example()
    {
        var input =
            "Sehr geehrte Damen und Herren von [ORG_1],\n\nHiermit bestreite ich die Forderung von [AMOUNT_1] im Zusammenhang mit der Betreibung [CASE_ID_1].\n\nFreundliche Grüsse\n[PERSON_1]";
        var result = Restorer.Restore(input, Dict());
        Assert.Equal(
            "Sehr geehrte Damen und Herren von Stiftung Beispiel,\n\nHiermit bestreite ich die Forderung von CHF 4'500.– im Zusammenhang mit der Betreibung 2024-118734.\n\nFreundliche Grüsse\nAnna Müller",
            result.Text
        );
        Assert.Empty(result.Issues);
        Assert.Equal(4, result.Restored.Count);
    }

    [Fact]
    public void Restores_parts()
    {
        var result = Restorer.Restore(
            "Liebe [PERSON_1_FIRST], Frau [PERSON_1_LAST], [ORG_1_SHORT]",
            Dict()
        );
        Assert.Equal("Liebe Anna, Frau Müller, Beispiel", result.Text);
    }

    [Fact]
    public void Unknown_placeholders_are_kept_and_reported()
    {
        var result = Restorer.Restore("Hallo [PERSON_7] und [ORG_1_ABBR].", Dict());
        Assert.Equal("Hallo [PERSON_7] und [ORG_1_ABBR].", result.Text);
        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, i => Assert.Equal(RestoreIssueKind.Unknown, i.Kind));
    }

    [Theory]
    [InlineData("Hallo PERSON_1, wie geht's?")]
    [InlineData("Hallo [PERSON 1], wie geht's?")]
    [InlineData("Hallo [Person_1], wie geht's?")]
    [InlineData("Hallo (PERSON_1), wie geht's?")]
    [InlineData("Hallo {PERSON_1}, wie geht's?")]
    [InlineData("Hallo [PERSON-1], wie geht's?")]
    [InlineData("Hallo [PERSON_1, wie geht's?")]
    [InlineData("Hallo PERSON_1_FIRST, wie geht's?")]
    public void Malformed_placeholders_are_reported_not_guessed(string input)
    {
        var result = Restorer.Restore(input, Dict());
        Assert.DoesNotContain("Anna", result.Text, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.Kind == RestoreIssueKind.Malformed);
    }

    [Fact]
    public void Ordinary_text_has_no_issues()
    {
        var result = Restorer.Restore(
            "Die Personalabteilung hat am Datum 3 geantwortet. Organisation ok.",
            Dict()
        );
        Assert.Empty(result.Issues);
    }
}
