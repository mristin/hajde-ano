using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Services;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.ViewModels;

/// <summary>Create or unlock the encrypted vault.</summary>
public sealed partial class UnlockViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action _onUnlocked;

    public UnlockViewModel(AppServices services, Action onUnlocked)
    {
        _services = services;
        _onUnlocked = onUnlocked;
        IsNewVault = !Vault.Exists(services.DataDirectory);
    }

    public bool IsNewVault { get; }

    public string Title =>
        IsNewVault ? "Create a password for your local case vault" : "Unlock your case vault";

    public string Hint =>
        IsNewVault
            ? "All cases and placeholder mappings are encrypted with a key derived from this password (AES-256-GCM, PBKDF2). There is no way to recover a lost password."
            : "Your cases are stored encrypted on this computer only.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private string _confirmPassword = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private bool _isBusy;

    private bool CanUnlock() =>
        !IsBusy && Password.Length > 0 && (!IsNewVault || ConfirmPassword.Length > 0);

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        Error = null;
        if (IsNewVault)
        {
            if (Password != ConfirmPassword)
            {
                Error = "The passwords do not match.";
                return;
            }

            if (Password.Length < 8)
            {
                Error = "Please use at least 8 characters.";
                return;
            }
        }

        IsBusy = true;
        try
        {
            var password = Password;
            var dir = _services.DataDirectory;
            var isNew = IsNewVault;
            _services.Vault = await Task.Run(() =>
                isNew ? Vault.Create(dir, password) : Vault.Open(dir, password)
            );
            Password = "";
            ConfirmPassword = "";
            _onUnlocked();
        }
        catch (UnauthorizedAccessException)
        {
            Error = "Wrong password.";
        }
        catch (VaultCorruptException e)
        {
            // Our own messages only; they contain no private data.
            Error =
                $"The vault is damaged and cannot be opened: {e.Message}. Restore the folder from a backup.";
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException)
        {
            Error = $"Could not open the vault ({e.GetType().Name}).";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
