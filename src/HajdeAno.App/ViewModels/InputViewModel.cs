using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Services;
using HajdeAno.Core;
using HajdeAno.Core.Llm;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.ViewModels;

/// <summary>Screen 2: paste or open the original text and run detection.</summary>
public sealed partial class InputViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _autosave;
    private CancellationTokenSource? _cts;
    private CaseFile? _draftCase;
    private bool _dirty;
    private bool _loadingDraft;
    private DateTime _firstUnsavedChange;

    /// <summary>Save after this pause in typing…</summary>
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(1);

    /// <summary>…but at least this often while typing continuously.</summary>
    public static readonly TimeSpan AutosaveMaxDelay = TimeSpan.FromSeconds(10);

    public InputViewModel(WorkspaceViewModel workspace)
    {
        _workspace = workspace;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateElapsed();
        _autosave = new DispatcherTimer { Interval = AutosaveDelay };
        _autosave.Tick += (_, _) => FlushDraft();
        RefreshModelInfo();
    }

    public void Dispose()
    {
        _timer.Stop();
        _autosave.Stop();
        _cts?.Dispose();
    }

    public WorkspaceViewModel Workspace => _workspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    private string _text = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand), nameof(CancelCommand))]
    private bool _isBusy;

    /// <summary>Whether the draft has been saved (shown next to the text box).</summary>
    [ObservableProperty]
    private string? _draftStatus;

    partial void OnTextChanged(string value)
    {
        if (_loadingDraft || _draftCase is null)
        {
            return;
        }

        if (!_dirty)
        {
            _dirty = true;
            _firstUnsavedChange = DateTime.UtcNow;
        }

        DraftStatus = "Unsaved changes…";
        if (DateTime.UtcNow - _firstUnsavedChange >= AutosaveMaxDelay)
        {
            FlushDraft();
            return;
        }

        _autosave.Stop();
        _autosave.Start();
    }

    /// <summary>Switch to the draft of another case, saving the current one first.</summary>
    public void LoadDraft(CaseFile? caseFile)
    {
        FlushDraft();
        _draftCase = caseFile;
        CancelConfirm();
        _loadingDraft = true;
        try
        {
            Text = caseFile?.Draft ?? "";
        }
        finally
        {
            _loadingDraft = false;
        }

        DraftStatus = string.IsNullOrEmpty(Text) ? null : "Restored from the case.";
    }

    /// <summary>
    /// Save pending changes of the draft into the case file. Nothing is written if
    /// the text did not actually change. Errors are reported, never thrown, since this also
    /// runs from a timer and when the window closes.
    /// </summary>
    public void FlushDraft()
    {
        _autosave.Stop();
        var session = _workspace.Services.Session;
        if (!_dirty || _draftCase is null || session is null)
        {
            return;
        }

        var draft = Text.Length == 0 ? null : Text;
        if (draft == _draftCase.Draft)
        {
            _dirty = false;
            DraftStatus = draft is null ? null : "Saved in the case.";
            return;
        }

        var previous = _draftCase.Draft;
        _draftCase.Draft = draft;
        try
        {
            session.Save(_draftCase);
            _dirty = false;
            DraftStatus = draft is null ? null : "Saved in the case.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep the changes pending; the next change or flush tries again.
            _draftCase.Draft = previous;
            DraftStatus =
                $"Could not save the text ({e.GetType().Name}). Retrying on the next change.";
        }
    }

    /// <summary>Drop the draft without saving (used before deleting its case).</summary>
    public void ForgetDraft()
    {
        _autosave.Stop();
        _dirty = false;
        _draftCase = null;
        CancelConfirm();
        _loadingDraft = true;
        try
        {
            Text = "";
        }
        finally
        {
            _loadingDraft = false;
        }

        DraftStatus = null;
    }

    [ObservableProperty]
    private string? _progress;

    /// <summary>Overall progress in percent.</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string? _elapsed;

    [ObservableProperty]
    private string? _error;

    /// <summary>Which model will be used, and where it is.</summary>
    [ObservableProperty]
    private string _modelInfo = "";

    public void RefreshModelInfo() =>
        ModelInfo = ModelService.Describe(_workspace.Services.Settings);

    private void UpdateElapsed()
    {
        var t = _stopwatch.Elapsed;
        Elapsed = $"Elapsed {(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void SetProgress(string message, double? fraction)
    {
        Progress = message;
        IsProgressIndeterminate = fraction is null;
        if (fraction is not null)
        {
            ProgressValue = Math.Round(fraction.Value * 100, 1);
        }
    }

    /// <summary>A question waiting for the user's confirmation (null if none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    private string? _confirmQuestion;

    public bool IsConfirming => ConfirmQuestion is not null;

    private Func<Task>? _confirmedAction;

    /// <summary>Run the action, but ask first if it would discard existing text.</summary>
    private Task RunReplacingTextAsync(string question, Func<Task> action)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return action();
        }

        ConfirmQuestion = question;
        _confirmedAction = action;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var action = _confirmedAction;
        ConfirmQuestion = null;
        _confirmedAction = null;
        if (action is not null)
        {
            await action();
        }
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        ConfirmQuestion = null;
        _confirmedAction = null;
    }

    [RelayCommand]
    private Task NewAsync() =>
        RunReplacingTextAsync(
            "Start a new text? The current text of this case will be deleted.",
            () =>
            {
                SetText("");
                return Task.CompletedTask;
            }
        );

    [RelayCommand]
    private Task OpenFileAsync() =>
        RunReplacingTextAsync(
            "Open another file? The current text of this case will be replaced.",
            async () =>
            {
                var path = await _workspace.Ui.PickTextFileAsync();
                if (path is null)
                {
                    return;
                }

                try
                {
                    SetText(await File.ReadAllTextAsync(path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The message may contain the path, which may contain names.
                    Error = $"Could not read the file ({e.GetType().Name}).";
                }
            }
        );

    /// <summary>
    /// Export the original text to a file outside the session. The text of the case itself
    /// is always saved automatically in the session.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var path = await _workspace.Ui.PickSaveTextFileAsync("case.txt");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, Text);
            Error = null;
            DraftStatus = "Exported to the file.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The message may contain the path, which may contain names.
            Error = $"Could not save the file ({e.GetType().Name}).";
        }
    }

    /// <summary>Replace the text; it is saved to the case right away.</summary>
    private void SetText(string text)
    {
        Text = text;
        Error = null;
        FlushDraft();
    }

    private bool CanDetect() => !IsBusy && !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task DetectAsync()
    {
        var caseFile = _workspace.CurrentCase;
        if (caseFile is null)
        {
            Error = "Create or select a case first.";
            return;
        }

        Error = null;
        IsBusy = true;
        _workspace.IsDetecting = true;
        RefreshModelInfo();
        _cts = new CancellationTokenSource();
        _stopwatch.Restart();
        UpdateElapsed();
        _timer.Start();
        try
        {
            var settings = _workspace.Services.Settings;
            IEntityExtractor? extractor = _workspace.Services.ExtractorOverride;
            if (extractor is null)
            {
                var models = _workspace.Services.Models;
                var name = Path.GetFileName(AppPaths.ResolveModel(settings.ModelPath) ?? "");
                SetProgress(
                    models.IsLoadedFor(settings)
                        ? $"Model {name} is already loaded."
                        : $"Loading model {name}…",
                    models.IsLoadedFor(settings) ? 0 : null
                );
                try
                {
                    var loadProgress = new Progress<float>(f =>
                        SetProgress($"Loading model {name}… {f * 100:F0} %", null)
                    );
                    extractor = await models.GetAsync(settings, loadProgress, _cts.Token);
                }
                catch (FileNotFoundException e)
                {
                    Error = e.Message;
                    return;
                }
            }

            var text = Text;
            var working = caseFile.Dictionary.Clone();
            var preexisting = working.Entities.Select(e => e.Id).ToHashSet();
            var anonymizer = new Anonymizer(extractor, settings.ToOptions());

            // Reports arrive for every generated token; only show the latest one.
            AnalysisProgress? latest = null;
            var pending = 0;
            var progress = new SyncProgress<AnalysisProgress>(p =>
            {
                latest = p;
                if (Interlocked.Exchange(ref pending, 1) == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Interlocked.Exchange(ref pending, 0);
                        if (latest is { } l)
                        {
                            SetProgress(l.Message, l.Fraction);
                        }
                    });
                }
            });

            var token = _cts.Token;
            var result = await Task.Run(
                () => anonymizer.AnalyzeAsync(text, working, progress, token),
                token
            );
            if (
                _workspace.CurrentCase?.Id != caseFile.Id
                || _workspace.Services.Session?.Contains(caseFile.Id) != true
            )
            {
                // Never let the names of one case end up in another (or a deleted) case.
                Error = "The case was changed during detection; the result was discarded.";
                return;
            }

            _workspace.Review.Load(caseFile.Id, result, working, preexisting, anonymizer.Options);
            _workspace.SelectedTab = WorkspaceViewModel.ReviewTab;
            var total = _stopwatch.Elapsed;
            _workspace.Status =
                $"Detection finished in {(int)total.TotalMinutes}:{total.Seconds:00}.";
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
#pragma warning disable CA1031 // A failed detection must not crash the program (and print details).
        catch (Exception e)
#pragma warning restore CA1031
        {
            Error = $"Detection failed because of an internal error ({e.GetType().Name}).";
        }
        finally
        {
            _workspace.IsDetecting = false;
            _timer.Stop();
            _stopwatch.Stop();
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();
}
