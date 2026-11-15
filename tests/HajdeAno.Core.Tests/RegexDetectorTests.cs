namespace HajdeAno.Core.Tests;

public class RegexDetectorTests
{
    private static List<(string Text, Category Category)> Detect(string text) =>
        SpanMerger
            .Merge(text, RegexDetector.Detect(text))
            .Select(d => (d.Text, d.Category))
            .ToList();

    [Theory]
    [InlineData(
        "Mail an anna.mueller@example.ch bitte.",
        "anna.mueller@example.ch",
        Category.Email
    )]
    [InlineData("Tel. 079 123 45 67.", "079 123 45 67", Category.Phone)]
    [InlineData("Tel. +41 44 123 45 67", "+41 44 123 45 67", Category.Phone)]
    [InlineData("Tel. 044/123.45.67", "044/123.45.67", Category.Phone)]
    [InlineData("AHV 756.1234.5678.97 ok", "756.1234.5678.97", Category.SocialSecurityNumber)]
    [InlineData("IBAN CH93 0076 2011 6238 5295 7", "CH93 0076 2011 6238 5295 7", Category.Iban)]
    [InlineData("am 14.02.2024 erhalten", "14.02.2024", Category.Date)]
    [InlineData("seit dem 1. März 2019", "1. März 2019", Category.Date)]
    [InlineData("am 2024-03-01", "2024-03-01", Category.Date)]
    [InlineData("geb. 03.07.2008", "03.07.2008", Category.DateOfBirth)]
    [InlineData("Geburtsdatum: 3.7.1980", "3.7.1980", Category.DateOfBirth)]
    [InlineData("verlangt CHF 4'500.– für", "CHF 4'500.–", Category.Amount)]
    [InlineData("Betrag Fr. 1’280.50", "Fr. 1’280.50", Category.Amount)]
    [InlineData("nur 300 Franken", "300 Franken", Category.Amount)]
    [InlineData("an der Bahnhofstrasse 12, 8001", "Bahnhofstrasse 12", Category.Address)]
    [InlineData("an der Seestrasse 4 in 6300 Zug", "Seestrasse 4", Category.Address)]
    [InlineData("Adresse: Sonnenrain 7a, Bern", "Sonnenrain 7a", Category.Address)]
    [InlineData("Die Verwaltung verlangt CHF 4500 für", "CHF 4500", Category.Amount)]
    [InlineData("Postfach 1234, 8021 Zürich", "Postfach 1234", Category.Address)]
    [InlineData("Siehe www.beispiel.ch.", "www.beispiel.ch", Category.Url)]
    [InlineData("Betreibung Nr. 2024-118734 des", "2024-118734", Category.DebtCollectionReference)]
    [InlineData("Kundennummer: KD-449120", "KD-449120", Category.CustomerNumber)]
    [InlineData("Policen-Nr. 88-776655-1", "88-776655-1", Category.CustomerNumber)]
    [InlineData("Urteil 2C_456/2023 vom", "2C_456/2023", Category.CaseNumber)]
    [InlineData("Geschäftsnummer FV230045-L", "FV230045-L", Category.CaseNumber)]
    [InlineData("ZEMIS 23456789.0", "23456789.0", Category.PermitNumber)]
    [InlineData("hat Ausweis B Nr. 12345678.", "12345678", Category.PermitNumber)]
    [InlineData("Geschäftsnummer CIV 25 1234 vom", "CIV 25 1234", Category.CaseNumber)]
    public void Detects(string text, string expected, Category category)
    {
        var found = Detect(text);
        Assert.Contains((expected, category), found);
    }

    [Fact]
    public void Splits_postal_code_and_city()
    {
        var found = Detect("Bahnhofstrasse 12, 8001 Zürich");
        Assert.Contains(("8001", Category.PostalCode), found);
        Assert.Contains(("Zürich", Category.City), found);
    }

    [Fact]
    public void Flags_long_numbers_for_review()
    {
        var detections = RegexDetector.Detect("Referenzcode 99887766554 im Text");
        Assert.Contains(detections, d => d.Text == "99887766554");
    }

    [Theory]
    [InlineData("CH93 0076 2011 6238 5295 7", true)]
    [InlineData("CH9300762011623852957", true)]
    [InlineData("CH94 0076 2011 6238 5295 7", false)]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    public void Validates_iban(string iban, bool valid) =>
        Assert.Equal(valid, RegexDetector.IsValidIban(iban));

    [Fact]
    public void Does_not_flag_ordinary_prose()
    {
        var found = Detect("Die Mieterin hat die Wohnung sauber übergeben und wartet auf Antwort.");
        Assert.Empty(found);
    }
}
