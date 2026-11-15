using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Controls;
using HajdeAno.Core;
using HajdeAno.Core.Documents;

namespace HajdeAno.App.ViewModels;

/// <summary>Screen 4: restore placeholders in a response from the online service.</summary>
public sealed partial class RestoreViewModel(WorkspaceViewModel workspace) : ViewModelBase
{
    public WorkspaceViewModel Workspace => workspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string _input = "";

    /// <summary>The Word document being restored (its bytes, with placeholders only).</summary>
    private byte[]? _word;

    private bool _settingInput;

    /// <summary>Name of the opened Word document (null if the answer is plain text).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWord))]
    [NotifyCanExecuteChangedFor(nameof(SaveWordCommand))]
    private string? _wordFileName;

    public bool IsWord => WordFileName is not null;

    partial void OnInputChanged(string value)
    {
        if (!_settingInput && _word is not null)
        {
            // The text box now differs from the document; restore the text instead.
            _word = null;
            WordFileName = null;
            Message =
                "You edited the text, so the Word document was set aside. Restore and copy the text, or open the document again.";
        }
    }

    private void SetInput(string text)
    {
        _settingInput = true;
        try
        {
            Input = text;
        }
        finally
        {
            _settingInput = false;
        }
    }

    /// <summary>Open an answer: a Word document, or a text or Markdown file.</summary>
    [RelayCommand]
    private async Task OpenAnswerAsync()
    {
        var path = await workspace.Ui.PickAnswerFileAsync();
        if (path is null)
        {
            return;
        }

        var current = workspace.CurrentCase;
        if (current is null)
        {
            Message = "Select a case first.";
            return;
        }

        try
        {
            if (string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var output = new MemoryStream();
                var result = WordRestorer.Restore(
                    new MemoryStream(bytes),
                    output,
                    current.Dictionary
                );
                _word = bytes;
                SetInput(result.Text);
                WordFileName = Path.GetFileName(path);
                Show(result.Preview);
                Summary =
                    $"Word document: {result.Restored} placeholder(s) restored"
                    + (
                        result.Preview.Issues.Count > 0
                            ? $", {result.Preview.Issues.Count} warning(s)."
                            : "."
                    );
                Message = null;
            }
            else
            {
                _word = null;
                WordFileName = null;
                SetInput(await File.ReadAllTextAsync(path));
                Restore();
            }
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The message may contain the path, which may contain names.
            Message =
                $"Could not read the answer ({e.GetType().Name}). Is it a Word (.docx), text or Markdown file?";
        }
    }

    private bool CanSaveWord() => WordFileName is not null;

    /// <summary>Write the restored Word document (with the original names, unencrypted).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveWord))]
    private async Task SaveWordAsync()
    {
        var current = workspace.CurrentCase;
        if (_word is null || current is null)
        {
            return;
        }

        var suggested = Path.GetFileNameWithoutExtension(WordFileName) + " (restored).docx";
        var path = await workspace.Ui.PickSaveWordFileAsync(suggested);
        if (path is null)
        {
            return;
        }

        try
        {
            using var output = new MemoryStream();
            WordRestorer.Restore(new MemoryStream(_word), output, current.Dictionary);
            await File.WriteAllBytesAsync(path, output.ToArray());
            Message =
                Issues.Count > 0
                    ? "Restored Word document saved. Note: some placeholders could not be restored."
                    : "Restored Word document saved.";
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Message = $"Could not save the Word document ({e.GetType().Name}).";
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string _output = "";

    [ObservableProperty]
    private IReadOnlyList<TextSegment> _outputSegments = [];

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string? _message;

    public ObservableCollection<string> Issues { get; } = [];

    public void Clear()
    {
        _word = null;
        WordFileName = null;
        SetInput("");
        Output = "";
        OutputSegments = [];
        Issues.Clear();
        Summary = null;
        Message = null;
    }

    private bool CanRestore() => !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        var current = workspace.CurrentCase;
        if (current is null)
        {
            Message = "Select a case first.";
            return;
        }

        var result = Restorer.Restore(Input, current.Dictionary);
        Show(result);
        Summary =
            $"Restored {result.Restored.Count} placeholder(s)"
            + (result.Issues.Count > 0 ? $", {result.Issues.Count} warning(s)." : ".");
        Message = null;
    }

    /// <summary>Show the restored text with the restored values and the warnings highlighted.</summary>
    private void Show(RestoreResult result)
    {
        Output = result.Text;

        var segments = new List<TextSegment>();
        var pos = 0;
        foreach (var r in result.Restored)
        {
            if (r.Start > pos)
            {
                segments.Add(new TextSegment(Output[pos..r.Start], SegmentKind.Plain));
            }

            segments.Add(
                new TextSegment(Output.Substring(r.Start, r.Length), SegmentKind.Placeholder)
            );
            pos = r.Start + r.Length;
        }

        if (pos < Output.Length)
        {
            segments.Add(new TextSegment(Output[pos..], SegmentKind.Plain));
        }

        OutputSegments = segments;

        Issues.Clear();
        foreach (var issue in result.Issues)
        {
            Issues.Add(
                issue.Kind == RestoreIssueKind.Unknown
                    ? $"Unknown placeholder {issue.Text}: not in this case, left unchanged."
                    : $"Modified placeholder \"{issue.Text}\": not restored, please fix it manually."
            );
        }
    }

    private bool CanCopy() => Output.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        await workspace.Ui.SetClipboardTextAsync(Output);
        Message =
            Issues.Count > 0
                ? "Copied. Note: some placeholders could not be restored."
                : "Restored text copied.";
    }
}
