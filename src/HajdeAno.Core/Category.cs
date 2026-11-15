namespace HajdeAno.Core;

/// <summary>Categories of sensitive information.</summary>
public enum Category
{
    Person,
    Family,
    Organization,
    OrganizationAbbreviation,
    GovernmentAgency,
    Court,
    Address,
    City,
    PostalCode,
    Country,
    Nationality,
    Phone,
    Email,
    Url,
    Date,
    DateOfBirth,
    Amount,
    Iban,
    CaseNumber,
    DebtCollectionReference,
    CustomerNumber,
    SocialSecurityNumber,
    PermitNumber,
    Other,
}

/// <summary>Static metadata about categories.</summary>
public static class Categories
{
    private sealed record Info(
        string LlmLabel,
        string PlaceholderPrefix,
        string DisplayName,
        bool IsIndirect
    );

    private static readonly Dictionary<Category, Info> Infos = new()
    {
        [Category.Person] = new("PERSON", "PERSON", "Person", false),
        [Category.Family] = new("FAMILY", "FAMILY", "Family name", false),
        [Category.Organization] = new("ORGANIZATION", "ORG", "Organization", false),
        [Category.OrganizationAbbreviation] = new(
            "ORG_ABBREVIATION",
            "ORG",
            "Organization abbreviation",
            false
        ),
        [Category.GovernmentAgency] = new(
            "GOVERNMENT_AGENCY",
            "AGENCY",
            "Government agency",
            false
        ),
        [Category.Court] = new("COURT", "COURT", "Court", false),
        [Category.Address] = new("ADDRESS", "ADDRESS", "Street address", false),
        [Category.City] = new("CITY", "CITY", "City / municipality", true),
        [Category.PostalCode] = new("POSTAL_CODE", "POSTAL_CODE", "Postal code", true),
        [Category.Country] = new("COUNTRY", "COUNTRY", "Country", true),
        [Category.Nationality] = new("NATIONALITY", "NATIONALITY", "Nationality", true),
        [Category.Phone] = new("PHONE", "PHONE", "Phone number", false),
        [Category.Email] = new("EMAIL", "EMAIL", "E-mail address", false),
        [Category.Url] = new("URL", "URL", "Website / URL", false),
        [Category.Date] = new("DATE", "DATE", "Date", true),
        [Category.DateOfBirth] = new("DATE_OF_BIRTH", "BIRTHDATE", "Date of birth", false),
        [Category.Amount] = new("AMOUNT", "AMOUNT", "Financial amount", true),
        [Category.Iban] = new("IBAN", "IBAN", "IBAN / bank account", false),
        [Category.CaseNumber] = new("CASE_NUMBER", "CASE_ID", "Legal case number", false),
        [Category.DebtCollectionReference] = new(
            "DEBT_COLLECTION_REF",
            "CASE_ID",
            "Debt collection reference",
            false
        ),
        [Category.CustomerNumber] = new("CUSTOMER_NUMBER", "CUSTOMER_ID", "Customer number", false),
        [Category.SocialSecurityNumber] = new(
            "SOCIAL_SECURITY_NUMBER",
            "SSN",
            "Social security (AHV) number",
            false
        ),
        [Category.PermitNumber] = new(
            "PERMIT_NUMBER",
            "PERMIT",
            "Immigration permit number",
            false
        ),
        [Category.Other] = new("OTHER", "ID", "Other identifying information", false),
    };

    private static readonly Dictionary<string, Category> ByLabel = Infos.ToDictionary(
        kv => kv.Value.LlmLabel,
        kv => kv.Key,
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>All categories in declaration order.</summary>
    public static IReadOnlyList<Category> All { get; } = Enum.GetValues<Category>();

    /// <summary>Label used in the LLM prompt and output.</summary>
    public static string LlmLabel(this Category category) => Infos[category].LlmLabel;

    /// <summary>Prefix of the placeholder, e.g. <c>PERSON</c> for <c>[PERSON_1]</c>.</summary>
    public static string PlaceholderPrefix(this Category category) =>
        Infos[category].PlaceholderPrefix;

    /// <summary>Human-readable name.</summary>
    public static string DisplayName(this Category category) => Infos[category].DisplayName;

    /// <summary>
    /// Indirect identifiers are only replaced in strict privacy mode
    /// (dates, amounts, nationalities and locations).
    /// </summary>
    public static bool IsIndirect(this Category category) => Infos[category].IsIndirect;

    /// <summary>Parse the label as returned by the LLM; unknown labels map to <see cref="Category.Other"/>.</summary>
    public static Category FromLlmLabel(string label)
    {
        var normalized = label.Trim().Replace(' ', '_').Replace('-', '_');
        if (ByLabel.TryGetValue(normalized, out var category))
        {
            return category;
        }

        // Be lenient with common synonyms that small models produce.
        return normalized.ToUpperInvariant() switch
        {
            "NAME" or "FIRST_NAME" or "LAST_NAME" or "SURNAME" or "FULL_NAME" => Category.Person,
            "COMPANY" or "ORG" or "FOUNDATION" or "COOPERATIVE" => Category.Organization,
            "AGENCY" or "AUTHORITY" => Category.GovernmentAgency,
            "STREET" or "STREET_ADDRESS" or "LOCATION" => Category.Address,
            "MUNICIPALITY" or "TOWN" or "PLACE" => Category.City,
            "ZIP" or "ZIP_CODE" or "POSTCODE" => Category.PostalCode,
            "PHONE_NUMBER" or "TELEPHONE" => Category.Phone,
            "E_MAIL" or "MAIL" => Category.Email,
            "WEBSITE" => Category.Url,
            "BIRTHDATE" or "DOB" => Category.DateOfBirth,
            "MONEY" or "CURRENCY" => Category.Amount,
            "BANK_ACCOUNT" or "ACCOUNT_NUMBER" => Category.Iban,
            "CASE_ID" or "REFERENCE" or "FILE_NUMBER" => Category.CaseNumber,
            "AHV" or "SSN" => Category.SocialSecurityNumber,
            "PERMIT" => Category.PermitNumber,
            _ => Category.Other,
        };
    }
}
