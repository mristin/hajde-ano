using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Services;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.ViewModels;

/// <summary>A session in the list of recently opened sessions.</summary>
public sealed partial class RecentSessionViewModel(SessionPickerViewModel picker, string directory)
{
    public string Directory { get; } = directory;

    public string Name { get; } = Session.NameOf(directory);

    /// <summary>Whether the folder still holds a session (it may have been moved or deleted).</summary>
    public bool IsAvailable { get; } = Session.IsSession(directory);

    public string Details => IsAvailable ? Directory : $"{Directory} (not found)";

    [RelayCommand]
    private void Open() => picker.OpenSession(Directory);

    [RelayCommand]
    private void Remove() => picker.RemoveRecent(this);
}

/// <summary>
/// Start screen, like the project picker of an IDE: create a session in a new folder, open
/// an existing session folder, or reopen a recent one.
/// </summary>
public sealed partial class SessionPickerViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly IUiServices _ui;
    private readonly Action _onOpened;

    public SessionPickerViewModel(AppServices services, IUiServices ui, Action onOpened)
    {
        _services = services;
        _ui = ui;
        _onOpened = onOpened;
        foreach (var directory in services.Settings.RecentSessions)
        {
            Recent.Add(new RecentSessionViewModel(this, directory));
        }
    }

    public ObservableCollection<RecentSessionViewModel> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    [ObservableProperty]
    private string? _error;

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var directory = await _ui.PickFolderAsync("Choose an empty folder for the new session");
        if (directory is not null)
        {
            CreateSession(directory);
        }
    }

    [RelayCommand]
    private async Task OpenExistingSessionAsync()
    {
        var directory = await _ui.PickFolderAsync("Open a session folder");
        if (directory is not null)
        {
            OpenSession(directory);
        }
    }

    /// <summary>
    /// Create a session in <paramref name="directory"/> and open it. If the folder already
    /// holds a session, that session is opened.
    /// </summary>
    public void CreateSession(string directory)
    {
        if (Session.IsSession(directory))
        {
            OpenSession(directory);
            return;
        }

        Use(() => Session.Create(directory));
    }

    /// <summary>Open the session in <paramref name="directory"/>.</summary>
    public void OpenSession(string directory) => Use(() => Session.Open(directory));

    public void RemoveRecent(RecentSessionViewModel item)
    {
        Recent.Remove(item);
        _services.Settings.RemoveRecentSession(item.Directory);
        SaveSettings();
        OnPropertyChanged(nameof(HasRecent));
    }

    private void Use(Func<Session> open)
    {
        Error = null;
        Session session;
        try
        {
            session = open();
        }
        catch (SessionException e)
        {
            // Our own messages only; they contain no paths.
            Error = e.Message + ".";
            return;
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The message may contain the path, which may contain names.
            Error = $"Could not open the session folder ({e.GetType().Name}).";
            return;
        }

        _services.Session = session;
        _services.Settings.AddRecentSession(session.Directory);
        SaveSettings();
        _onOpened();
    }

    private void SaveSettings()
    {
        try
        {
            _services.SaveSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only the list of recent sessions is lost; the session itself is fine.
        }
    }
}
