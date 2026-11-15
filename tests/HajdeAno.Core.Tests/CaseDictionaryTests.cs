namespace HajdeAno.Core.Tests;

public class CaseDictionaryTests
{
    [Fact]
    public void Person_gets_full_first_and_last_placeholders()
    {
        var dict = new CaseDictionary();
        var a = dict.Assign("Anna Müller", Category.Person);
        Assert.Equal("[PERSON_1]", a.Placeholder);

        Assert.Equal("[PERSON_1_FIRST]", dict.Assign("Anna", Category.Person).Placeholder);
        Assert.Equal("[PERSON_1_LAST]", dict.Assign("Müller", Category.Person).Placeholder);
        Assert.Equal("Anna", dict.Resolve("PERSON_1_FIRST"));
        Assert.Equal("Müller", dict.Resolve("PERSON_1_LAST"));
        Assert.Equal("Anna Müller", dict.Resolve("PERSON_1"));

        var person = dict.Find("PERSON_1")!;
        Assert.Equal("FAMILY_1", person.FamilyId);
        Assert.Equal("Müller", dict.Resolve("FAMILY_1"));
    }

    [Fact]
    public void Shared_surname_maps_to_family_and_is_flagged()
    {
        var dict = new CaseDictionary();
        dict.Assign("Anna Müller", Category.Person);
        dict.Assign("Peter Müller", Category.Person);

        var a = dict.Assign("Müller", Category.Person);
        Assert.Equal("[FAMILY_1]", a.Placeholder);
        Assert.True(a.NeedsReview);
        Assert.Equal("[PERSON_2]", dict.Assign("Peter Müller", Category.Person).Placeholder);
    }

    [Fact]
    public void Same_entity_same_placeholder_case_insensitive_and_whitespace()
    {
        var dict = new CaseDictionary();
        var a = dict.Assign("Stiftung Beispiel", Category.Organization);
        var b = dict.Assign("stiftung   Beispiel", Category.Organization);
        Assert.Equal(a.Placeholder, b.Placeholder);
    }

    [Fact]
    public void Organization_short_name_is_derived_and_flagged()
    {
        var dict = new CaseDictionary();
        Assert.Equal(
            "[ORG_1]",
            dict.Assign("Stiftung Beispiel", Category.Organization).Placeholder
        );
        var shortName = dict.Assign("Beispiel", Category.Organization);
        Assert.Equal("[ORG_1_SHORT]", shortName.Placeholder);
        Assert.True(shortName.NeedsReview);
        Assert.Equal("Beispiel", dict.Resolve("ORG_1_SHORT"));
    }

    [Fact]
    public void Abbreviation_links_by_initials()
    {
        var dict = new CaseDictionary();
        dict.Assign("Gemeinnützige Baugenossenschaft Limmattal", Category.Organization);
        var a = dict.Assign("GBL", Category.OrganizationAbbreviation);
        Assert.Equal("[ORG_1_ABBR]", a.Placeholder);
        Assert.Equal("GBL", dict.Resolve("ORG_1_ABBR"));
    }

    [Fact]
    public void Abbreviation_links_by_hint()
    {
        var dict = new CaseDictionary();
        dict.Assign("Sozialversicherungsanstalt Bern", Category.GovernmentAgency);
        var hint = dict.Find("AGENCY_1");
        var a = dict.Assign("SVA", Category.OrganizationAbbreviation, hint);
        Assert.Equal("[AGENCY_1_ABBR]", a.Placeholder);
    }

    [Fact]
    public void Unlinked_abbreviation_becomes_own_org()
    {
        var dict = new CaseDictionary();
        Assert.Equal("[ORG_1]", dict.Assign("XYZ", Category.OrganizationAbbreviation).Placeholder);
    }

    [Fact]
    public void Misspelled_full_name_becomes_alias_for_review()
    {
        var dict = new CaseDictionary();
        dict.Assign("Zeynep Yildirim", Category.Person);
        var a = dict.Assign("Zeynep Yildirm", Category.Person);
        Assert.Equal("[PERSON_1]", a.Placeholder);
        Assert.True(a.NeedsReview);
        Assert.Contains("Zeynep Yildirm", dict.Find("PERSON_1")!.Aliases);
    }

    [Fact]
    public void Initial_with_surname_links_to_person()
    {
        var dict = new CaseDictionary();
        dict.Assign("Anna Müller", Category.Person);
        var a = dict.Assign("A. Müller", Category.Person);
        Assert.Equal("[PERSON_1]", a.Placeholder);
        Assert.True(a.NeedsReview);
    }

    [Fact]
    public void Generic_categories_are_numbered_per_prefix()
    {
        var dict = new CaseDictionary();
        Assert.Equal("[DATE_1]", dict.Assign("14.02.2024", Category.Date).Placeholder);
        Assert.Equal("[DATE_2]", dict.Assign("15.02.2024", Category.Date).Placeholder);
        Assert.Equal("[DATE_1]", dict.Assign("14.02.2024", Category.Date).Placeholder);
        Assert.Equal(
            "[CASE_ID_1]",
            dict.Assign("2024-1", Category.DebtCollectionReference).Placeholder
        );
        Assert.Equal("[CASE_ID_2]", dict.Assign("CIV 25 1", Category.CaseNumber).Placeholder);
        Assert.Equal("[POSTAL_CODE_1]", dict.Assign("8001", Category.PostalCode).Placeholder);
        Assert.Equal("8001", dict.Resolve("POSTAL_CODE_1"));
    }

    [Theory]
    [InlineData("Anna Müller", "Anna", "Müller")]
    [InlineData("Anna Maria Müller", "Anna Maria", "Müller")]
    [InlineData("Jan van der Berg", "Jan", "van der Berg")]
    [InlineData("Fatima Zahra El Idrissi", "Fatima Zahra", "El Idrissi")]
    public void Splits_names(string name, string first, string last)
    {
        var (f, l) = CaseDictionary.SplitName(name.Split(' '));
        Assert.Equal(first, f);
        Assert.Equal(last, l);
    }

    [Fact]
    public void Json_round_trip()
    {
        var dict = new CaseDictionary();
        dict.Assign("Anna Müller", Category.Person);
        dict.Assign("Stiftung Beispiel", Category.Organization);
        dict.Ignore("Wohnung");
        var json = dict.ToJson();
        Assert.Contains(
            "\"full_name\"",
            json.Replace("\"value\"", "\"full_name\"", StringComparison.Ordinal),
            StringComparison.Ordinal
        );
        var copy = CaseDictionary.FromJson(json);
        Assert.Equal(dict.Entities.Count, copy.Entities.Count);
        Assert.Equal("[PERSON_1_FIRST]", copy.Assign("Anna", Category.Person).Placeholder);
        Assert.True(copy.IsIgnored("wohnung"));
    }

    [Fact]
    public void Prune_removes_unused_new_entities_but_keeps_families()
    {
        var dict = new CaseDictionary();
        dict.Assign("Anna Müller", Category.Person);
        var preexisting = dict.Entities.Select(e => e.Id).ToHashSet();
        dict.Assign("Peter Keller", Category.Person);
        dict.Assign("Stiftung Beispiel", Category.Organization);
        dict.Prune(preexisting, new HashSet<string> { "PERSON_2" });
        Assert.NotNull(dict.Find("PERSON_2"));
        Assert.NotNull(dict.Find(dict.Find("PERSON_2")!.FamilyId!));
        Assert.Null(dict.Find("ORG_1"));
        Assert.NotNull(dict.Find("PERSON_1"));
    }

    [Fact]
    public void Removing_entity_frees_nothing_else()
    {
        var dict = new CaseDictionary();
        dict.Assign("Anna Müller", Category.Person);
        dict.Remove("FAMILY_1");
        Assert.Null(dict.Find("PERSON_1")!.FamilyId);
        Assert.Null(dict.Resolve("FAMILY_1"));
    }
}
