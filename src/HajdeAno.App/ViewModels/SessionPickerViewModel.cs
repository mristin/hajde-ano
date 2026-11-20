using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Services;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.ViewModels;

/// <summary>A session in the list of recently opened sessions.</summary>
public sealed partial class RecentSessionViewModel
{
    private readonly SessionPickerViewModel _picker;

    public RecentSessionViewModel(SessionPickerViewModel picker, string directory)
    {
        _picker = picker;
        Directory = directory;
        Name = Session.NameOf(directory);
        IsAvailable = Session.IsSession(directory);
        if (IsAvailable)
        {
            try
            {
                IsEncrypted = Session.IsEncryptedSession(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Reported when the session is opened.
            }
        }
    }

    public string Directory { get; }

    public string Name { get; }

    /// <summary>Whether the folder still holds a session (it may have been moved or deleted).</summary>
    public bool IsAvailable { get; }

    public bool IsEncrypted { get; }

    public string Details =>
        !IsAvailable ? $"{Directory} (not found)"
        : IsEncrypted ? $"Encrypted · {Directory}"
        : Directory;

    [RelayCommand]
    private void Open() => _picker.OpenSession(Directory);

    [RelayCommand]
    private void Remove() => _picker.RemoveRecent(this);
}

/// <summary>What the session picker currently shows.</summary>
public enum SessionPickerMode
{
    /// <summary>The recent sessions.</summary>
    Choose,

    /// <summary>Options of a new session (encryption).</summary>
    Create,

    /// <summary>The password of an encrypted session.</summary>
    Unlock,
}

/// <summary>
/// Start screen, like the project picker of an IDE: create a session in a new folder
/// (optionally encrypted), open an existing session folder, or reopen a recent one.
/// </summary>
public sealed partial class SessionPickerViewModel : ViewModelBase
{
    public const int MinPasswordLength = 8;

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
    [NotifyPropertyChangedFor(
        nameof(IsChoosing),
        nameof(IsCreating),
        nameof(IsUnlocking),
        nameof(ChooseError),
        nameof(FormError)
    )]
    private SessionPickerMode _mode;

    public bool IsChoosing => Mode == SessionPickerMode.Choose;

    public bool IsCreating => Mode == SessionPickerMode.Create;

    public bool IsUnlocking => Mode == SessionPickerMode.Unlock;

    /// <summary>Folder of the session being created or unlocked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingName))]
    private string? _pendingDirectory;

    public string PendingName => PendingDirectory is null ? "" : Session.NameOf(PendingDirectory);

    /// <summary>Whether the new session is encrypted.</summary>
    [ObservableProperty]
    private bool _encrypt;

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _confirmPassword = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(NewSessionCommand),
        nameof(OpenExistingSessionCommand),
        nameof(ConfirmCreateCommand),
        nameof(ConfirmUnlockCommand),
        nameof(CancelCommand)
    )]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseError), nameof(FormError))]
    private string? _error;

    /// <summary>The error, shown next to the buttons of the start screen.</summary>
    public string? ChooseError => IsChoosing ? Error : null;

    /// <summary>The error, shown in the form of a new or encrypted session.</summary>
    public string? FormError => IsChoosing ? null : Error;

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task NewSessionAsync()
    {
        var directory = await _ui.PickFolderAsync("Choose an empty folder for the new session");
        if (directory is not null)
        {
            CreateSession(directory);
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenExistingSessionAsync()
    {
        var directory = await _ui.PickFolderAsync("Open a session folder");
        if (directory is not null)
        {
            OpenSession(directory);
        }
    }

    /// <summary>
    /// Start creating a session in <paramref name="directory"/>: show its options. If the
    /// folder already holds a session, that session is opened instead.
    /// </summary>
    public void CreateSession(string directory)
    {
        Error = null;
        Show(SessionPickerMode.Choose, null);
        if (Session.IsSession(directory))
        {
            OpenSession(directory);
            return;
        }

        try
        {
            if (
                Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()
            )
            {
                Error = "The folder is not empty. Choose an empty folder for a new session.";
                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = $"Could not read the folder ({e.GetType().Name}).";
            return;
        }

        Show(SessionPickerMode.Create, directory);
    }

    /// <summary>
    /// Open the session in <paramref name="directory"/>; an encrypted session asks for its
    /// password first.
    /// </summary>
    public void OpenSession(string directory)
    {
        Error = null;
        Show(SessionPickerMode.Choose, null);
        bool encrypted;
        try
        {
            encrypted = Session.IsEncryptedSession(directory);
        }
        catch (SessionException e)
        {
            // Our own messages only; they contain no paths.
            Error = e.Message + ".";
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The message may contain the path, which may contain names.
            Error = $"Could not open the session folder ({e.GetType().Name}).";
            return;
        }

        if (encrypted)
        {
            Show(SessionPickerMode.Unlock, directory);
            return;
        }

        try
        {
            Use(Session.Open(directory));
        }
        catch (Exception e) when (ErrorFor(e) is { } message)
        {
            Error = message;
        }
    }

    private void Show(SessionPickerMode mode, string? directory)
    {
        PendingDirectory = directory;
        Encrypt = false;
        Password = "";
        ConfirmPassword = "";
        Mode = mode;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ConfirmCreateAsync()
    {
        Error = null;
        var directory = PendingDirectory!;
        string? password = null;
        if (Encrypt)
        {
            if (Password.Length < MinPasswordLength)
            {
                Error = $"Please use at least {MinPasswordLength} characters.";
                return;
            }

            if (Password != ConfirmPassword)
            {
                Error = "The passwords do not match.";
                return;
            }

            password = Password;
        }

        await OpenInBackgroundAsync(() => Session.Create(directory, password));
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ConfirmUnlockAsync()
    {
        Error = null;
        if (Password.Length == 0)
        {
            Error = "Please enter the password of the session.";
            return;
        }

        var directory = PendingDirectory!;
        var password = Password;
        await OpenInBackgroundAsync(() => Session.Open(directory, password));
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void Cancel()
    {
        Error = null;
        Show(SessionPickerMode.Choose, null);
    }

    public void RemoveRecent(RecentSessionViewModel item)
    {
        Recent.Remove(item);
        _services.Settings.RemoveRecentSession(item.Directory);
        SaveSettings();
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>
    /// Create or open the session in the background (deriving the key from a password takes
    /// a moment), then switch to it. Errors are shown, never thrown.
    /// </summary>
    private async Task OpenInBackgroundAsync(Func<Session> open)
    {
        IsBusy = true;
        Session? session = null;
        try
        {
            session = await Task.Run(open);
        }
        catch (Exception e) when (ErrorFor(e) is { } message)
        {
            Error = message;
        }
        finally
        {
            IsBusy = false;
        }

        if (session is not null)
        {
            Use(session);
        }
    }

    /// <summary>The message to show for an expected error, or null for any other.</summary>
    private string? ErrorFor(Exception e) =>
        e switch
        {
            UnauthorizedAccessException when IsUnlocking => "Wrong password.",

            // Our own messages only; they contain no paths.
            SessionException => e.Message + ".",

            // The message may contain the path, which may contain names.
            IOException or UnauthorizedAccessException or ArgumentException =>
                $"Could not open the session folder ({e.GetType().Name}).",
            _ => null,
        };

    private void Use(Session session)
    {
        Password = "";
        ConfirmPassword = "";
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
