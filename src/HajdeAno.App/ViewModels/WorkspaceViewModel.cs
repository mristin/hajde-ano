using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Services;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.ViewModels;

/// <summary>A case in the list.</summary>
public sealed class CaseItemViewModel(CaseFile file)
{
    public CaseFile File { get; } = file;

    public string Name => File.Name;

    public string Details =>
        $"{File.Dictionary.Entities.Count} entities · {File.DocumentCount} documents · {File.Modified.LocalDateTime:g}";
}

/// <summary>The main workspace of an open session: cases on the left, screens as tabs.</summary>
public sealed partial class WorkspaceViewModel : ViewModelBase
{
    public const int InputTab = 0;
    public const int ReviewTab = 1;
    public const int RestoreTab = 2;
    public const int EntitiesTab = 3;
    public const int SettingsTab = 4;

    private readonly Action _closeSession;

    public WorkspaceViewModel(AppServices services, IUiServices ui, Action closeSession)
    {
        Services = services;
        Ui = ui;
        _closeSession = closeSession;
        Input = new InputViewModel(this);
        Review = new ReviewViewModel(this);
        Restore = new RestoreViewModel(this);
        Entities = new EntitiesViewModel(this);
        Settings = new SettingsViewModel(this);
        ReloadCases();
    }

    public AppServices Services { get; }

    public IUiServices Ui { get; }

    public string SessionName => Services.Session?.Name ?? "";

    public string SessionDirectory => Services.Session?.Directory ?? "";

    public string SessionLabel =>
        Services.Session?.IsEncrypted == true ? "SESSION · ENCRYPTED" : "SESSION";

    public InputViewModel Input { get; }

    public ReviewViewModel Review { get; }

    public RestoreViewModel Restore { get; }

    public EntitiesViewModel Entities { get; }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<CaseItemViewModel> Cases { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCase), nameof(CaseTitle))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCaseCommand))]
    private CaseItemViewModel? _selectedCase;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCaseCommand))]
    private string _newCaseName = "";

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>
    /// While the detection runs, the case cannot be switched, created or deleted, and the
    /// session cannot be closed,
    /// so that its result can only ever be saved into the case it was computed for.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitchCase))]
    [NotifyCanExecuteChangedFor(
        nameof(DeleteCaseCommand),
        nameof(CreateCaseCommand),
        nameof(CloseSessionCommand)
    )]
    private bool _isDetecting;

    public bool CanSwitchCase => !IsDetecting;

    public bool HasCase => SelectedCase is not null;

    public string CaseTitle => SelectedCase?.Name ?? "No case selected";

    public CaseFile? CurrentCase => SelectedCase?.File;

    private bool _keepWork;

    partial void OnSelectedCaseChanged(CaseItemViewModel? value)
    {
        if (_keepWork)
        {
            return;
        }

        IsConfirmingDelete = false;
        Input.LoadDraft(value?.File);
        Review.Clear();
        Restore.Clear();
        Entities.Reload();
    }

    public void ReloadCases(string? selectId = null)
    {
        var session = Services.Session;
        if (session is null)
        {
            return;
        }

        // The case objects are re-read from disk below; persist the draft first.
        Input.FlushDraft();

        var loaded = session.LoadAll();
        var cases = loaded.Cases;
        Cases.Clear();
        foreach (var c in cases)
        {
            Cases.Add(new CaseItemViewModel(c));
        }

        SelectedCase = Cases.FirstOrDefault(c => c.File.Id == selectId) ?? Cases.FirstOrDefault();
        if (loaded.Unreadable > 0)
        {
            Status = $"{loaded.Unreadable} case file(s) could not be read and were skipped.";
        }
    }

    /// <summary>Persist the current case and refresh the list entry.</summary>
    public void SaveCurrentCase()
    {
        if (CurrentCase is null || Services.Session is null)
        {
            return;
        }

        Services.Session.Save(CurrentCase);
        var index = Cases.IndexOf(SelectedCase!);
        var item = new CaseItemViewModel(CurrentCase);
        _keepWork = true;
        try
        {
            Cases[index] = item;
            SelectedCase = item;
        }
        finally
        {
            _keepWork = false;
        }
    }

    private bool CanCreateCase() => !IsDetecting && !string.IsNullOrWhiteSpace(NewCaseName);

    [RelayCommand(CanExecute = nameof(CanCreateCase))]
    private void CreateCase()
    {
        var file = new CaseFile { Name = NewCaseName.Trim() };
        Services.Session!.Save(file);
        NewCaseName = "";
        ReloadCases(file.Id);
        SelectedTab = InputTab;
        Status = "Case created.";
    }

    private bool CanDeleteCase() => !IsDetecting && SelectedCase is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteCase))]
    private void DeleteCase()
    {
        if (!IsConfirmingDelete)
        {
            IsConfirmingDelete = true;
            Status =
                "Click \"Delete case\" again to permanently delete the case and all its mappings.";
            return;
        }

        Input.ForgetDraft();
        Services.Session!.Delete(SelectedCase!.File.Id);
        IsConfirmingDelete = false;
        ReloadCases();
        Status = "Case deleted.";
    }

    private bool CanCloseSession() => !IsDetecting;

    [RelayCommand(CanExecute = nameof(CanCloseSession))]
    private void CloseSession() => _closeSession();
}
