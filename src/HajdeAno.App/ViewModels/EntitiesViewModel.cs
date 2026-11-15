using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.Core;

namespace HajdeAno.App.ViewModels;

/// <summary>An editable entity of the case dictionary.</summary>
public sealed partial class EntityItemViewModel : ViewModelBase
{
    public EntityItemViewModel(Entity entity)
    {
        Id = entity.Id;
        Category = entity.Category;
        _value = entity.Value;
        _firstName = entity.FirstName ?? "";
        _lastName = entity.LastName ?? "";
        _shortName = entity.ShortName ?? "";
        _abbreviation = entity.Abbreviation ?? "";
        _aliases = string.Join("; ", entity.Aliases);
        FamilyId = entity.FamilyId;
    }

    public string Id { get; }

    public string Placeholder => $"[{Id}]";

    public Category Category { get; }

    public string CategoryName => Category.DisplayName();

    public string? FamilyId { get; }

    public bool IsPerson => Category == Category.Person;

    public bool IsOrganization =>
        Category
            is Category.Organization
                or Category.OrganizationAbbreviation
                or Category.GovernmentAgency
                or Category.Court;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private string _firstName;

    [ObservableProperty]
    private string _lastName;

    [ObservableProperty]
    private string _shortName;

    [ObservableProperty]
    private string _abbreviation;

    /// <summary>Semicolon-separated aliases.</summary>
    [ObservableProperty]
    private string _aliases;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public void ApplyTo(Entity e)
    {
        e.Value = Value.Trim();
        e.FirstName = NullIfEmpty(FirstName);
        e.LastName = NullIfEmpty(LastName);
        e.ShortName = NullIfEmpty(ShortName);
        e.Abbreviation = NullIfEmpty(Abbreviation);
        e.Aliases = Aliases
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();
    }
}

/// <summary>Screen: view and edit the entities of the case (aliases, abbreviations, …).</summary>
public sealed partial class EntitiesViewModel(WorkspaceViewModel workspace) : ViewModelBase
{
    public WorkspaceViewModel Workspace => workspace;

    public ObservableCollection<EntityItemViewModel> Entities { get; } = [];

    public ObservableCollection<string> IgnoredTerms { get; } = [];

    public IReadOnlyList<Category> AllCategories => Categories.All;

    [ObservableProperty]
    private Category _newCategory = Category.Person;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newValue = "";

    [ObservableProperty]
    private string? _message;

    public void Reload()
    {
        Entities.Clear();
        IgnoredTerms.Clear();
        var dict = workspace.CurrentCase?.Dictionary;
        if (dict is null)
        {
            return;
        }

        foreach (var e in dict.Entities)
        {
            Entities.Add(new EntityItemViewModel(e));
        }

        foreach (var t in dict.IgnoredTerms)
        {
            IgnoredTerms.Add(t);
        }
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewValue);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var dict = workspace.CurrentCase?.Dictionary;
        if (dict is null)
        {
            return;
        }

        var a = dict.Assign(NewValue.Trim(), NewCategory);
        workspace.SaveCurrentCase();
        NewValue = "";
        Reload();
        Message = $"Mapped to {a.Placeholder}.";
    }

    [RelayCommand]
    private void Save()
    {
        var dict = workspace.CurrentCase?.Dictionary;
        if (dict is null)
        {
            return;
        }

        foreach (var item in Entities)
        {
            var e = dict.Find(item.Id);
            if (e is not null)
            {
                item.ApplyTo(e);
            }
        }

        dict.Invalidate();
        workspace.SaveCurrentCase();
        Message = "Saved.";
    }

    [RelayCommand]
    private void Delete(EntityItemViewModel item)
    {
        workspace.CurrentCase?.Dictionary.Remove(item.Id);
        workspace.SaveCurrentCase();
        Reload();
        Message = $"Deleted {item.Placeholder}.";
    }

    [RelayCommand]
    private void Unignore(string term)
    {
        workspace.CurrentCase?.Dictionary.IgnoredTerms.Remove(term);
        workspace.SaveCurrentCase();
        Reload();
    }
}
