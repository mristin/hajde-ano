using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.Core;
using HajdeAno.Core.Llm;

namespace HajdeAno.App.ViewModels;

/// <summary>Screen 2: paste or open the original text and run detection.</summary>
public sealed partial class InputViewModel(WorkspaceViewModel workspace)
    : ViewModelBase,
        IDisposable
{
    public void Dispose() => _cts?.Dispose();

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    private string _text = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _progress;

    [ObservableProperty]
    private string? _error;

    public WorkspaceViewModel Workspace => workspace;

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = await workspace.Ui.PickTextFileAsync();
        if (path is not null)
        {
            Text = await File.ReadAllTextAsync(path);
        }
    }

    private bool CanDetect() => !IsBusy && !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task DetectAsync()
    {
        if (workspace.CurrentCase is null)
        {
            Error = "Create or select a case first.";
            return;
        }

        Error = null;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var settings = workspace.Services.Settings;
            IEntityExtractor? extractor = workspace.Services.ExtractorOverride;
            if (extractor is null)
            {
                Progress = "Loading the local model…";
                try
                {
                    extractor = await workspace.Services.Models.GetAsync(settings, _cts.Token);
                }
                catch (FileNotFoundException e)
                {
                    Error = e.Message;
                    return;
                }
            }

            var text = Text;
            var working = workspace.CurrentCase.Dictionary.Clone();
            var preexisting = working.Entities.Select(e => e.Id).ToHashSet();
            var anonymizer = new Anonymizer(extractor, settings.ToOptions());
            var progress = new Progress<string>(p => Progress = p);
            var token = _cts.Token;
            var result = await Task.Run(
                () => anonymizer.AnalyzeAsync(text, working, progress, token),
                token
            );
            workspace.Review.Load(result, working, preexisting, anonymizer.Options);
            workspace.SelectedTab = WorkspaceViewModel.ReviewTab;
            Progress = null;
        }
        catch (OperationCanceledException)
        {
            Progress = "Cancelled.";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // Never show exception details that could echo private input.
            Error =
                $"Detection failed ({e.GetType().Name}). Try a larger context size or a smaller text.";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Clear() => Text = "";
}
