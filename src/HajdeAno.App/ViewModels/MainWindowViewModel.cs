using CommunityToolkit.Mvvm.ComponentModel;
using HajdeAno.App.Services;

namespace HajdeAno.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly IUiServices _ui;

    [ObservableProperty]
    private ViewModelBase _current;

    public MainWindowViewModel(AppServices services, IUiServices ui)
    {
        _services = services;
        _ui = ui;
        _current = new UnlockViewModel(services, OnUnlocked);
    }

    private void OnUnlocked() => Current = new WorkspaceViewModel(_services, _ui, Lock);

    /// <summary>Persist unsaved drafts (before locking or exiting).</summary>
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
        else if (Current is UnlockViewModel unlock)
        {
            unlock.Error = message;
        }
    }

    public void Lock()
    {
        FlushDrafts();
        _services.Vault?.Dispose();
        _services.Vault = null;
        _services.Models.Unload();
        Current = new UnlockViewModel(_services, OnUnlocked);
    }
}
