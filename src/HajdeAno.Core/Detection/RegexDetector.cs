using System.Numerics;
using System.Text.RegularExpressions;

namespace HajdeAno.Core;

/// <summary>
/// Deterministic detection of structured sensitive information, tuned for German and
/// Swiss German administrative texts.
/// </summary>
public static partial class RegexDetector
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    private const string Months =
        "Januar|Jänner|Februar|März|Maerz|April|Mai|Juni|Juli|August|September|Oktober"
        + "|November|Dezember|Jan|Feb|Mär|Mrz|Apr|Jun|Jul|Aug|Sep|Sept|Okt|Nov|Dez";

    [GeneratedRegex(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+\.[\p{L}]{2,}", Opts)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}@])(?:https?://[^\s<>""]+|www\.[^\s<>""]+|[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)*\.(?:ch|de|at|li|com|org|net|eu|info|swiss|io|fr|it)(?:/[^\s<>""]*)?)(?![\p{L}\p{N}@])",
        Opts
    )]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?\b", Opts)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"\b756[.\s]?\d{4}[.\s]?\d{4}[.\s]?\d{2}\b", Opts)]
    private static partial Regex AhvRegex();

    [GeneratedRegex(
        @"(?<![\w+])(?:(?:\+|00)\d{2,3}[\s./\-]?(?:\(0\))?[\s./\-]?\d{1,4}|0\d{2,4})(?:[\s./\-]?\d{2,4}){2,4}(?!\w)",
        Opts
    )]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(
        @"\b(?:\d{1,2}\.\s?\d{1,2}\.\s?(?:\d{4}|\d{2})(?!\d)|\d{4}-\d{2}-\d{2}\b|\d{1,2}\.?\s?(?:"
            + Months
            + @")\.?\s\d{4}\b|(?:"
            + Months
            + @")\s\d{4}\b)",
        Opts | RegexOptions.IgnoreCase
    )]
    private static partial Regex DateRegex();

    [GeneratedRegex(
        @"(?:geb\.|geboren(?:\s+am)?|Geburtsdatum:?|Geb\.-?Datum:?|Jahrgang|\*)\s*$",
        Opts | RegexOptions.IgnoreCase
    )]
    private static partial Regex BirthContextRegex();

    [GeneratedRegex(
        @"(?:(?:CHF|SFr\.|Fr\.|EUR|€|USD|\$)\s?(?:\d{1,3}(?:[’'`. ]\d{3})+|\d+)(?![\d’'`])(?:[.,](?:\d{1,2}|[-–]{1,2}))?|\b(?:\d{1,3}(?:[’'`. ]\d{3})+|\d+)(?:[.,](?:\d{1,2}|[-–]{1,2}))?\s?(?:CHF|Franken|Fr\.|EUR|Euro|€))",
        Opts
    )]
    private static partial Regex AmountRegex();

    [GeneratedRegex(
        @"(?<=^|[\s,(])(?<code>(?:CH-|D-|A-)?(?:[1-9]\d{3}|\d{5}))\s+(?<city>\p{Lu}[\p{L}\-]+(?:\s(?:am|an\sder|bei|im|ob|a\.\s?A\.|b\.)\s?\p{Lu}[\p{L}\-]+)?)",
        Opts
    )]
    private static partial Regex PostalCityRegex();

    [GeneratedRegex(
        @"\b(?:\p{Lu}[\p{L}\-]*(?:strasse|straße|str\.|weg|gasse|platz|allee|ring|rain|halde|matte|quai|steig|stutz|graben|hof|berg)|\p{Lu}[\p{L}]+\s(?:Strasse|Straße|Weg|Gasse|Platz|Allee))\s\d{1,4}(?:\s?[a-zA-Z]\b)?(?!\d)",
        Opts
    )]
    private static partial Regex StreetRegex();

    [GeneratedRegex(@"\b(?:Postfach|Case\spostale)\s\d{1,6}\b", Opts)]
    private static partial Regex PostBoxRegex();

    [GeneratedRegex(
        @"(?<kw>Kunden(?:nummer|-?Nr\.?)|Kd\.-?Nr\.?|Vertrags(?:nummer|-?Nr\.?)|Polic(?:e|en)(?:nummer|-?Nr\.?)|Rechnungs(?:nummer|-?Nr\.?)|Mitglied(?:er)?(?:nummer|-?Nr\.?)|Versicherten(?:nummer|-?Nr\.?)|Konto(?:nummer|-?Nr\.?)|Referenz(?:nummer)?|Ref\.|Dossier(?:nummer|-?Nr\.?)?|Akten(?:zeichen|nummer)|Gesch(?:äfts|\.)-?(?:nummer|Nr\.?)|Verfahrens(?:nummer|-?Nr\.?)|Fall(?:nummer|-?Nr\.?)|Prozess(?:nummer|-?Nr\.?)|Betreibungs?(?:nummer|-?Nr\.?)|Betreibung|Zahlungsbefehl|Pfändung|Verlustschein|Ausweis(?:nummer|-?Nr\.?)?(?:\s+[BCFGLNS](?=\s))?|ZEMIS(?:-?Nr\.?)?|N-Nr\.?|Aufenthaltsbewilligung|Niederlassungsbewilligung)(?:\s+(?:Nr\.?|Nummer|No\.?))?\s*:?\s*(?<id>(?:[A-Z]{1,4}[\-.\s]?)?\d[\dA-Z]*(?:[./_\-][\dA-Z]+)*)",
        Opts | RegexOptions.IgnoreCase
    )]
    private static partial Regex KeywordIdRegex();

    [GeneratedRegex(@"\b\d[A-Z]_\d{1,5}/\d{4}\b", Opts)]
    private static partial Regex FederalCourtRegex();

    // Cantonal court numbers with spaces, e.g. "CIV 25 1234" or "ZK1 24 56".
    [GeneratedRegex(@"\b[A-Z]{2,4}\d?\s\d{2}\s\d{1,6}\b", Opts)]
    private static partial Regex SpacedCourtCaseRegex();

    [GeneratedRegex(
        @"\b(?=[A-Z0-9]*\d)[A-Z]{1,4}\d{2,}(?:[.\-/_][A-Z0-9]+)+\b|\b[A-Z]{1,4}\.\d{4}\.\d{1,6}\b",
        Opts
    )]
    private static partial Regex CourtCaseRegex();

    [GeneratedRegex(@"(?<![\d.'’])\d{6,}(?![\d.'’])", Opts)]
    private static partial Regex LongNumberRegex();

    /// <summary>Detect structured sensitive information in <paramref name="text"/>.</summary>
    public static IReadOnlyList<Detection> Detect(string text)
    {
        var result = new List<Detection>();

        void Add(int start, int length, Category category, bool needsReview = false)
        {
            if (length <= 0)
            {
                return;
            }

            var span = text.Substring(start, length);
            var trimmedEnd = span.TrimEnd('.', ',', ';', ':', ')', '!', '?', ' ', '\t', '\n', '\r');
            if (trimmedEnd.Length == 0)
            {
                return;
            }

            result.Add(
                new Detection(start, trimmedEnd, category, DetectionSource.Regex)
                {
                    NeedsReview = needsReview,
                    Note = needsReview ? "Pattern match; please verify" : null,
                }
            );
        }

        foreach (Match m in EmailRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.Email);
        }

        foreach (Match m in UrlRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.Url);
        }

        foreach (Match m in IbanRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.Iban, needsReview: !IsValidIban(m.Value));
        }

        foreach (Match m in AhvRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.SocialSecurityNumber);
        }

        foreach (Match m in PhoneRegex().Matches(text))
        {
            var digits = m.Value.Count(char.IsDigit);
            if (digits >= 9 && digits <= 15)
            {
                Add(m.Index, m.Length, Category.Phone);
            }
        }

        foreach (Match m in DateRegex().Matches(text))
        {
            var before = text[Math.Max(0, m.Index - 30)..m.Index];
            var category = BirthContextRegex().IsMatch(before)
                ? Category.DateOfBirth
                : Category.Date;
            Add(m.Index, m.Length, category);
        }

        foreach (Match m in AmountRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.Amount);
        }

        foreach (Match m in PostalCityRegex().Matches(text))
        {
            var code = m.Groups["code"];
            var city = m.Groups["city"];
            Add(code.Index, code.Length, Category.PostalCode);
            Add(city.Index, city.Length, Category.City);
        }

        foreach (Match m in StreetRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.Address);
        }

        foreach (Match m in PostBoxRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.Address);
        }

        foreach (Match m in KeywordIdRegex().Matches(text))
        {
            var id = m.Groups["id"];
            var kw = m.Groups["kw"].Value.ToLowerInvariant();
            var category = kw switch
            {
                _ when kw.StartsWith("betreibung", StringComparison.Ordinal)
                        || kw.StartsWith("zahlungsbefehl", StringComparison.Ordinal)
                        || kw.StartsWith("pfändung", StringComparison.Ordinal)
                        || kw.StartsWith("verlustschein", StringComparison.Ordinal) =>
                    Category.DebtCollectionReference,
                _ when kw.StartsWith("ausweis", StringComparison.Ordinal)
                        || kw.StartsWith("zemis", StringComparison.Ordinal)
                        || kw.StartsWith("n-nr", StringComparison.Ordinal)
                        || kw.Contains("bewilligung", StringComparison.Ordinal) =>
                    Category.PermitNumber,
                _ when kw.StartsWith("kunden", StringComparison.Ordinal)
                        || kw.StartsWith("kd", StringComparison.Ordinal)
                        || kw.StartsWith("vertrag", StringComparison.Ordinal)
                        || kw.StartsWith("polic", StringComparison.Ordinal)
                        || kw.StartsWith("rechnung", StringComparison.Ordinal)
                        || kw.StartsWith("mitglied", StringComparison.Ordinal)
                        || kw.StartsWith("versicherten", StringComparison.Ordinal)
                        || kw.StartsWith("konto", StringComparison.Ordinal) =>
                    Category.CustomerNumber,
                _ => Category.CaseNumber,
            };

            // Skip plain years and dates that follow a keyword (e.g. "Betreibung 2024").
            if (id.Value.Length >= 3 && !DateRegex().IsMatch(id.Value))
            {
                Add(id.Index, id.Length, category);
            }
        }

        foreach (Match m in FederalCourtRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.CaseNumber);
        }

        foreach (Match m in SpacedCourtCaseRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.CaseNumber);
        }

        foreach (Match m in CourtCaseRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.CaseNumber);
        }

        // Recall first: any long number is suspicious, but needs a human look.
        foreach (Match m in LongNumberRegex().Matches(text))
        {
            Add(m.Index, m.Length, Category.Other, needsReview: true);
        }

        return result;
    }

    /// <summary>Validate the IBAN checksum (ISO 13616, mod 97).</summary>
    public static bool IsValidIban(string iban)
    {
        var compact = new string(
            iban.Where(c => !char.IsWhiteSpace(c)).ToArray()
        ).ToUpperInvariant();
        if (compact.Length < 15 || compact.Length > 34)
        {
            return false;
        }

        var rearranged = compact[4..] + compact[..4];
        var digits = new System.Text.StringBuilder();
        foreach (var c in rearranged)
        {
            if (char.IsDigit(c))
            {
                digits.Append(c);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                digits.Append(c - 'A' + 10);
            }
            else
            {
                return false;
            }
        }

        return BigInteger.Parse(
                digits.ToString(),
                System.Globalization.CultureInfo.InvariantCulture
            ) % 97
            == 1;
    }
}
