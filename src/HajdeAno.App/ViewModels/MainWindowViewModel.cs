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

    public void Lock()
    {
        _services.Vault?.Dispose();
        _services.Vault = null;
        _services.Models.Unload();
        Current = new UnlockViewModel(_services, OnUnlocked);
    }
}
