using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HajdeAno.Core;

/// <summary>An entity of a case, e.g. a person or an organization.</summary>
public sealed class Entity
{
    /// <summary>Stable id, which is also the base placeholder, e.g. <c>PERSON_1</c>.</summary>
    public string Id { get; set; } = "";

    public Category Category { get; set; }

    /// <summary>Canonical full value, e.g. "Anna Müller".</summary>
    public string Value { get; set; } = "";

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    /// <summary>Short name of an organization, e.g. "Beispiel" for "Stiftung Beispiel".</summary>
    public string? ShortName { get; set; }

    /// <summary>Abbreviation of an organization, e.g. "SVA".</summary>
    public string? Abbreviation { get; set; }

    /// <summary>Id of the family entity (shared surname) of a person.</summary>
    public string? FamilyId { get; set; }

    /// <summary>Alternative spellings that map to the full placeholder.</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Placeholder token for the full entity, e.g. <c>[PERSON_1]</c>.</summary>
    [JsonIgnore]
    public string Placeholder => $"[{Id}]";
}

/// <summary>A text form that maps to a placeholder.</summary>
public sealed record SurfaceForm(
    string Text,
    string Placeholder,
    string EntityId,
    bool Ambiguous,
    string? Note
);

/// <summary>Result of assigning a placeholder to a detection.</summary>
public sealed record Assignment(
    string Placeholder,
    string EntityId,
    bool NeedsReview,
    string? Note
);

/// <summary>
/// The persistent per-case dictionary of entities. Guarantees that the same entity gets
/// the same placeholder across all documents of a case.
/// </summary>
public sealed partial class CaseDictionary
{
    internal const string PartFirst = "FIRST";
    internal const string PartLast = "LAST";
    internal const string PartShort = "SHORT";
    internal const string PartAbbr = "ABBR";

    private static readonly HashSet<string> LegalFormWords = new(
        [
            "stiftung",
            "genossenschaft",
            "wohnbaugenossenschaft",
            "baugenossenschaft",
            "wohngenossenschaft",
            "verein",
            "ag",
            "gmbh",
            "sa",
            "sàrl",
            "sarl",
            "kg",
            "&",
            "co",
            "co.",
            "holding",
            "verwaltung",
            "verwaltungen",
            "immobilien",
            "treuhand",
            "der",
            "die",
            "das",
            "für",
            "und",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly HashSet<string> NameParticles = new(
        [
            "van",
            "von",
            "de",
            "der",
            "den",
            "da",
            "di",
            "del",
            "della",
            "dos",
            "das",
            "du",
            "le",
            "la",
            "al",
            "el",
            "bin",
            "ibn",
            "ben",
            "ter",
            "zu",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private Dictionary<string, SurfaceForm>? _formsCache;

    public List<Entity> Entities { get; set; } = [];

    /// <summary>Keys (see <see cref="TextUtil.Key"/>) of terms the user marked as not sensitive.</summary>
    public List<string> IgnoredTerms { get; set; } = [];

    [GeneratedRegex(
        @"^(?<id>[A-Z]+(?:_[A-Z]+)*_\d+)(?:_(?<part>FIRST|LAST|SHORT|ABBR))?$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex TokenRegex();

    public Entity? Find(string id) => Entities.Find(e => e.Id == id);

    /// <summary>Call after editing entities directly.</summary>
    public void Invalidate() => _formsCache = null;

    public bool IsIgnored(string text) => IgnoredTerms.Contains(TextUtil.Key(text));

    public void Ignore(string text)
    {
        var key = TextUtil.Key(text);
        if (!IgnoredTerms.Contains(key))
        {
            IgnoredTerms.Add(key);
        }
    }

    /// <summary>Create a new entity with the next free id for its placeholder prefix.</summary>
    public Entity Create(Category category, string value)
    {
        var prefix = category.PlaceholderPrefix();
        var next =
            Entities
                .Select(e => ParseNumber(e.Id, prefix))
                .Where(n => n is not null)
                .Select(n => n!.Value)
                .DefaultIfEmpty(0)
                .Max() + 1;
        var entity = new Entity
        {
            Id = $"{prefix}_{next}",
            Category = category,
            Value = TextUtil.NormalizeWhitespace(value),
        };
        Entities.Add(entity);
        Invalidate();
        return entity;
    }

    private static int? ParseNumber(string id, string prefix)
    {
        if (!id.StartsWith(prefix + "_", StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(id.AsSpan(prefix.Length + 1), out var n) ? n : null;
    }

    public void Remove(string id)
    {
        Entities.RemoveAll(e => e.Id == id);
        foreach (var e in Entities.Where(e => e.FamilyId == id))
        {
            e.FamilyId = null;
        }

        Invalidate();
    }

    /// <summary>All text forms known to the dictionary, keyed by <see cref="TextUtil.Key"/>.</summary>
    public IReadOnlyDictionary<string, SurfaceForm> SurfaceForms()
    {
        if (_formsCache is not null)
        {
            return _formsCache;
        }

        var forms = new Dictionary<string, SurfaceForm>();

        void Add(string? text, string placeholder, string entityId, bool ambiguous, string? note)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var key = TextUtil.Key(text);
            if (forms.TryGetValue(key, out var existing))
            {
                if (existing.Placeholder != placeholder && !existing.Ambiguous)
                {
                    forms[key] = existing with
                    {
                        Ambiguous = true,
                        Note = $"\"{text}\" matches several entities",
                    };
                }

                return;
            }

            forms[key] = new SurfaceForm(
                TextUtil.NormalizeWhitespace(text),
                placeholder,
                entityId,
                ambiguous,
                note
            );
        }

        var persons = Entities.Where(e => e.Category == Category.Person).ToList();
        var lastNameCounts = persons
            .Where(p => p.LastName is not null)
            .GroupBy(p => TextUtil.Key(p.LastName!))
            .ToDictionary(g => g.Key, g => g.Count());
        var firstNameCounts = persons
            .Where(p => p.FirstName is not null)
            .GroupBy(p => TextUtil.Key(p.FirstName!))
            .ToDictionary(g => g.Key, g => g.Count());

        // Full values and aliases first: they are the most specific.
        foreach (var e in Entities.Where(e => e.Category != Category.Family))
        {
            Add(e.Value, e.Placeholder, e.Id, false, null);
            foreach (var alias in e.Aliases)
            {
                Add(alias, e.Placeholder, e.Id, false, null);
            }
        }

        foreach (var e in Entities)
        {
            switch (e.Category)
            {
                case Category.Person:
                    if (e.FirstName is not null)
                    {
                        var shared = firstNameCounts[TextUtil.Key(e.FirstName)] > 1;
                        Add(
                            e.FirstName,
                            $"[{e.Id}_{PartFirst}]",
                            e.Id,
                            shared,
                            shared
                                ? $"First name \"{e.FirstName}\" is shared by several persons"
                                : null
                        );
                    }

                    if (e.LastName is not null)
                    {
                        var shared = lastNameCounts[TextUtil.Key(e.LastName)] > 1;
                        if (shared && e.FamilyId is not null)
                        {
                            Add(
                                e.LastName,
                                $"[{e.FamilyId}]",
                                e.FamilyId,
                                true,
                                $"Surname \"{e.LastName}\" is shared by several persons; mapped to the family"
                            );
                        }
                        else
                        {
                            Add(e.LastName, $"[{e.Id}_{PartLast}]", e.Id, false, null);
                        }
                    }

                    break;
                case Category.Organization
                or Category.OrganizationAbbreviation
                or Category.GovernmentAgency
                or Category.Court:
                    Add(
                        e.ShortName,
                        $"[{e.Id}_{PartShort}]",
                        e.Id,
                        true,
                        $"Short name of \"{e.Value}\"; may be a common word"
                    );
                    Add(e.Abbreviation, $"[{e.Id}_{PartAbbr}]", e.Id, false, null);
                    break;
                default:
                    break;
            }
        }

        // Families last: a surname of a single known person maps to that person.
        foreach (var e in Entities.Where(e => e.Category == Category.Family))
        {
            Add(e.Value, e.Placeholder, e.Id, false, null);
            foreach (var alias in e.Aliases)
            {
                Add(alias, e.Placeholder, e.Id, false, null);
            }
        }

        _formsCache = forms;
        return forms;
    }

    /// <summary>
    /// Resolve a placeholder token (without brackets, e.g. <c>PERSON_1_FIRST</c>) to the
    /// original text, or null if it is unknown.
    /// </summary>
    public string? Resolve(string token)
    {
        var m = TokenRegex().Match(token);
        if (!m.Success)
        {
            return null;
        }

        var id = m.Groups["id"].Value;
        var entity = Find(id);
        if (entity is null && m.Groups["part"].Success)
        {
            // An id like "POSTAL_CODE_1" never has a part, but be defensive about ids that
            // happen to end in a part name.
            entity = Find(token);
            if (entity is not null)
            {
                return entity.Value;
            }
        }

        if (entity is null)
        {
            return null;
        }

        return m.Groups["part"].Success
            ? m.Groups["part"].Value switch
            {
                PartFirst => entity.FirstName,
                PartLast => entity.LastName,
                PartShort => entity.ShortName,
                PartAbbr => entity.Abbreviation,
                _ => null,
            }
            : entity.Value;
    }

    /// <summary>
    /// Assign a placeholder to the detection. Reuses known entities and creates new ones
    /// as necessary.
    /// </summary>
    /// <param name="text">Text of the detection.</param>
    /// <param name="category">Detected category.</param>
    /// <param name="hint">Optional entity the detection is likely related to
    /// (e.g. the organization preceding an abbreviation in parentheses).</param>
    public Assignment Assign(string text, Category category, Entity? hint = null)
    {
        var normalized = TextUtil.NormalizeWhitespace(text);
        var forms = SurfaceForms();
        if (forms.TryGetValue(TextUtil.Key(normalized), out var form))
        {
            return new Assignment(form.Placeholder, form.EntityId, form.Ambiguous, form.Note);
        }

        return category switch
        {
            Category.Person => AssignPerson(normalized),
            Category.Family => AssignFamily(normalized),
            Category.Organization or Category.GovernmentAgency or Category.Court =>
                AssignOrganization(normalized, category),
            Category.OrganizationAbbreviation => AssignAbbreviation(normalized, hint),
            _ => FromEntity(Create(category, normalized)),
        };
    }

    private static Assignment FromEntity(Entity e, bool needsReview = false, string? note = null) =>
        new(e.Placeholder, e.Id, needsReview, note);

    private Entity GetOrCreateFamily(string surname)
    {
        var key = TextUtil.Key(surname);
        return Entities.Find(e => e.Category == Category.Family && TextUtil.Key(e.Value) == key)
            ?? Create(Category.Family, surname);
    }

    private Assignment AssignFamily(string surname)
    {
        var family = GetOrCreateFamily(surname);
        return FromEntity(family);
    }

    private static bool IsFuzzyEqual(string a, string b)
    {
        var ka = TextUtil.StripDiacritics(TextUtil.Key(a));
        var kb = TextUtil.StripDiacritics(TextUtil.Key(b));
        if (ka == kb)
        {
            return true;
        }

        var len = Math.Min(ka.Length, kb.Length);
        if (len < 5)
        {
            return false;
        }

        var allowed = len >= 10 ? 2 : 1;
        return TextUtil.EditDistance(ka, kb) <= allowed;
    }

    private Assignment AssignPerson(string name)
    {
        var words = name.Split(' ');
        var persons = Entities.Where(e => e.Category == Category.Person).ToList();

        if (words.Length == 1)
        {
            foreach (var p in persons)
            {
                if (p.FirstName is not null && IsFuzzyEqual(name, p.FirstName))
                {
                    return new Assignment(
                        $"[{p.Id}_{PartFirst}]",
                        p.Id,
                        true,
                        $"Similar to first name \"{p.FirstName}\" of {p.Id}"
                    );
                }

                if (p.LastName is not null && IsFuzzyEqual(name, p.LastName))
                {
                    return new Assignment(
                        $"[{p.Id}_{PartLast}]",
                        p.Id,
                        true,
                        $"Similar to surname \"{p.LastName}\" of {p.Id}"
                    );
                }
            }

            var single = Create(Category.Person, name);
            return FromEntity(single, needsReview: false);
        }

        // "A. Müller": initial followed by a known surname.
        if (words.Length == 2 && words[0].Length == 2 && words[0][1] == '.')
        {
            var initial = words[0][0];
            var candidates = persons
                .Where(p =>
                    p.LastName is not null
                    && TextUtil.Key(p.LastName) == TextUtil.Key(words[1])
                    && p.FirstName is not null
                    && char.ToUpperInvariant(p.FirstName[0]) == char.ToUpperInvariant(initial)
                )
                .ToList();
            if (candidates.Count >= 1)
            {
                var p = candidates[0];
                p.Aliases.Add(name);
                Invalidate();
                return FromEntity(
                    p,
                    needsReview: true,
                    note: candidates.Count > 1
                        ? $"\"{name}\" matches several persons"
                        : $"Abbreviated form of \"{p.Value}\""
                );
            }
        }

        foreach (var p in persons)
        {
            if (IsFuzzyEqual(name, p.Value))
            {
                p.Aliases.Add(name);
                Invalidate();
                return FromEntity(p, needsReview: true, note: $"Similar to \"{p.Value}\"");
            }
        }

        var (first, last) = SplitName(words);
        var person = Create(Category.Person, name);
        person.FirstName = first;
        person.LastName = last;
        if (last is not null)
        {
            person.FamilyId = GetOrCreateFamily(last).Id;
        }

        Invalidate();
        return FromEntity(person);
    }

    /// <summary>Split a multi-word name into first and last name.</summary>
    internal static (string? First, string? Last) SplitName(string[] words)
    {
        if (words.Length < 2)
        {
            return (null, null);
        }

        var lastStart = words.Length - 1;
        for (var i = 1; i < words.Length - 1; i++)
        {
            if (NameParticles.Contains(words[i]))
            {
                lastStart = i;
                break;
            }
        }

        return (string.Join(' ', words[..lastStart]), string.Join(' ', words[lastStart..]));
    }

    private Assignment AssignOrganization(string name, Category category)
    {
        foreach (
            var o in Entities.Where(e =>
                e.Category
                    is Category.Organization
                        or Category.GovernmentAgency
                        or Category.Court
                        or Category.OrganizationAbbreviation
            )
        )
        {
            if (IsFuzzyEqual(name, o.Value))
            {
                o.Aliases.Add(name);
                Invalidate();
                return FromEntity(o, needsReview: true, note: $"Similar to \"{o.Value}\"");
            }
        }

        var org = Create(category, name);
        if (category == Category.Organization)
        {
            var distinctive = name.Split(' ')
                .Where(w => !LegalFormWords.Contains(w.TrimEnd(',')))
                .ToList();
            if (distinctive.Count == 1 && distinctive[0].Length >= 4 && distinctive[0] != name)
            {
                org.ShortName = distinctive[0];
            }
        }

        Invalidate();
        return FromEntity(org);
    }

    /// <summary>Initials of the capitalized words, e.g. "Sozialversicherungsanstalt Zürich" → "SZ".</summary>
    internal static string Initials(string name)
    {
        var parts = name.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return new string(
            parts.Where(p => char.IsUpper(p[0])).Select(p => char.ToUpperInvariant(p[0])).ToArray()
        );
    }

    private Assignment AssignAbbreviation(string abbr, Entity? hint)
    {
        var orgs = Entities
            .Where(e =>
                e.Category is Category.Organization or Category.GovernmentAgency or Category.Court
            )
            .ToList();

        var target =
            hint is not null && hint.Abbreviation is null && orgs.Contains(hint)
                ? hint
                : orgs.Find(o =>
                    o.Abbreviation is null
                    && Initials(o.Value).Length >= 2
                    && string.Equals(Initials(o.Value), abbr, StringComparison.OrdinalIgnoreCase)
                );

        if (target is not null)
        {
            target.Abbreviation = abbr;
            Invalidate();
            return new Assignment(
                $"[{target.Id}_{PartAbbr}]",
                target.Id,
                true,
                $"Linked abbreviation to \"{target.Value}\""
            );
        }

        return FromEntity(Create(Category.OrganizationAbbreviation, abbr));
    }

    /// <summary>
    /// Remove entities that were created after <paramref name="preexisting"/> was captured
    /// but are not referenced by any of <paramref name="usedIds"/>.
    /// </summary>
    public void Prune(IReadOnlySet<string> preexisting, IReadOnlySet<string> usedIds)
    {
        var keep = new HashSet<string>(preexisting);
        keep.UnionWith(usedIds);
        foreach (var e in Entities.Where(e => keep.Contains(e.Id)).ToList())
        {
            if (e.FamilyId is not null)
            {
                keep.Add(e.FamilyId);
            }
        }

        Entities.RemoveAll(e => !keep.Contains(e.Id));
        Invalidate();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static CaseDictionary FromJson(string json) =>
        JsonSerializer.Deserialize<CaseDictionary>(json, JsonOptions)
        ?? throw new InvalidDataException("Invalid case dictionary");

    public CaseDictionary Clone() => FromJson(ToJson());
}
