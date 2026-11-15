using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Controls;
using HajdeAno.Core;

namespace HajdeAno.App.ViewModels;

/// <summary>Screen 4: restore placeholders in a response from the online service.</summary>
public sealed partial class RestoreViewModel(WorkspaceViewModel workspace) : ViewModelBase
{
    public WorkspaceViewModel Workspace => workspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string _input = "";

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
        Input = "";
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

        Summary =
            $"Restored {result.Restored.Count} placeholder(s)"
            + (result.Issues.Count > 0 ? $", {result.Issues.Count} warning(s)." : ".");
        Message = null;
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
