using CommunityToolkit.Mvvm.ComponentModel;
using HajdeAno.App.Services;

namespace HajdeAno.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly IUiServices _ui;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private ViewModelBase _current;

    public MainWindowViewModel(AppServices services, IUiServices ui)
    {
        _services = services;
        _ui = ui;
        _current = NewPicker();
    }

    /// <summary>Window title: the name of the open session, if any.</summary>
    public string Title =>
        _services.Session is { } session ? $"{session.Name} – Hajde Ano" : "Hajde Ano";

    private SessionPickerViewModel NewPicker() => new(_services, _ui, OnSessionOpened);

    private void OnSessionOpened() =>
        Current = new WorkspaceViewModel(_services, _ui, CloseSession);

    /// <summary>Persist unsaved drafts (before closing the session or exiting).</summary>
    public void FlushDrafts()
    {
        if (Current is WorkspaceViewModel workspace)
        {
            workspace.Input.FlushDraft();
        }
    }

    /// <summary>Show that something went wrong, without any details that could be private.</summary>
    public void ReportInternalError(string errorType)
    {
        var message =
            $"An internal error occurred ({errorType}). Your work was saved where possible.";
        if (Current is WorkspaceViewModel workspace)
        {
            workspace.Status = message;
            try
            {
                FlushDrafts();
            }
#pragma warning disable CA1031 // Reporting an error must never throw another one.
            catch (Exception)
#pragma warning restore CA1031
            {
                workspace.Status = message.Replace(
                    " Your work was saved where possible.",
                    "",
                    StringComparison.Ordinal
                );
            }
        }
        else if (Current is SessionPickerViewModel picker)
        {
            picker.Error = message;
        }
    }

    /// <summary>Save, close the session and go back to the session picker.</summary>
    public void CloseSession()
    {
        FlushDrafts();
        _services.Session = null;
        Current = NewPicker();
    }
}
