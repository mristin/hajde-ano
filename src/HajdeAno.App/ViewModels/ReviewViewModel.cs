using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.App.Controls;
using HajdeAno.Core;

namespace HajdeAno.App.ViewModels;

/// <summary>All occurrences of the same text mapped to the same placeholder.</summary>
public sealed partial class DetectionGroupViewModel : ViewModelBase
{
    private readonly ReviewViewModel _owner;
    private bool _suppress;

    public DetectionGroupViewModel(ReviewViewModel owner, List<Detection> detections)
    {
        _owner = owner;
        Detections = detections;
        _category = detections[0].Category;
    }

    public List<Detection> Detections { get; }

    public string Text => TextUtil.NormalizeWhitespace(Detections[0].Text);

    public string Placeholder => Detections[0].Placeholder ?? "";

    public int Count => Detections.Count;

    public string CountText => Count == 1 ? "1×" : $"{Count}×";

    public string Sources => string.Join(", ", Detections.Select(d => d.Source).Distinct());

    public bool NeedsReview => Detections.Any(d => d.NeedsReview);

    public string? Note => Detections.Select(d => d.Note).FirstOrDefault(n => n is not null);

    public ReviewStatus Status => Detections[0].Status;

    public bool IsPendingReview => NeedsReview && Status == ReviewStatus.Pending;

    public bool IsApproved => Status == ReviewStatus.Approved;

    public bool IsRejected => Status == ReviewStatus.Rejected;

    public string StatusText =>
        Status switch
        {
            ReviewStatus.Approved => "approved",
            ReviewStatus.Rejected => "kept in clear",
            _ => NeedsReview ? "needs review" : "auto",
        };

    public IReadOnlyList<Category> AllCategories => Categories.All;

    [ObservableProperty]
    private Category _category;

    partial void OnCategoryChanged(Category value)
    {
        if (_suppress)
        {
            return;
        }

        _owner.ChangeCategory(this, value);
    }

    [RelayCommand]
    private void Approve() => _owner.SetStatus(this, ReviewStatus.Approved);

    [RelayCommand]
    private void Reject() => _owner.SetStatus(this, ReviewStatus.Rejected);

    public void Refresh()
    {
        _suppress = true;
        Category = Detections[0].Category;
        _suppress = false;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Screen 3: side-by-side review of the detections.</summary>
public sealed partial class ReviewViewModel(WorkspaceViewModel workspace) : ViewModelBase
{
    private CaseDictionary? _working;
    private HashSet<string> _preexisting = [];
    private AnonymizationOptions _options = new();
    private List<Detection> _detections = [];

    public WorkspaceViewModel Workspace => workspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private string _text = "";

    [ObservableProperty]
    private IReadOnlyList<TextSegment> _originalSegments = [];

    [ObservableProperty]
    private IReadOnlyList<TextSegment> _anonymizedSegments = [];

    [ObservableProperty]
    private string _anonymizedText = "";

    public ObservableCollection<DetectionGroupViewModel> Groups { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    private int _selectionStart;

    [ObservableProperty]
    private int _selectionEnd;

    [ObservableProperty]
    private Category _manualCategory = Category.Person;

    public IReadOnlyList<Category> AllCategories => Categories.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private bool _confirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyPropertyChangedFor(nameof(ReviewSummary))]
    private int _pendingCount;

    [ObservableProperty]
    private string? _message;

    public bool HasDocument => Text.Length > 0;

    public string ReviewSummary =>
        PendingCount == 0
            ? $"{Groups.Count} detected item(s). Nothing left to decide."
            : $"{PendingCount} item(s) need your decision before export.";

    public void Clear()
    {
        _working = null;
        _detections = [];
        Text = "";
        Groups.Clear();
        Warnings.Clear();
        Confirmed = false;
        Message = null;
        Refresh();
    }

    public void Load(
        AnalysisResult result,
        CaseDictionary working,
        HashSet<string> preexisting,
        AnonymizationOptions options
    )
    {
        _working = working;
        _preexisting = preexisting;
        _options = options;
        _detections = result.Detections;
        Text = result.Text;
        Warnings.Clear();
        foreach (var w in result.Warnings)
        {
            Warnings.Add(w);
        }

        Confirmed = false;
        Message = null;
        Rebuild();
    }

    private void Rebuild()
    {
        Groups.Clear();
        foreach (
            var g in _detections
                .GroupBy(d => (TextUtil.Key(d.Text), d.Placeholder))
                .OrderBy(g => g.Min(d => d.Start))
        )
        {
            Groups.Add(new DetectionGroupViewModel(this, g.ToList()));
        }

        Refresh();
    }

    private void Refresh()
    {
        PendingCount = Groups.Count(g => g.IsPendingReview);
        OnPropertyChanged(nameof(ReviewSummary));
        AnonymizedText = Anonymizer.Apply(Text, _detections);
        OriginalSegments = BuildOriginalSegments();
        AnonymizedSegments = BuildAnonymizedSegments();
    }

    private static SegmentKind KindOf(Detection d) =>
        d.Status switch
        {
            ReviewStatus.Rejected => SegmentKind.Rejected,
            ReviewStatus.Pending when d.NeedsReview => SegmentKind.Review,
            _ => SegmentKind.Replaced,
        };

    private List<TextSegment> BuildOriginalSegments()
    {
        var segments = new List<TextSegment>();
        var pos = 0;
        foreach (var d in _detections.OrderBy(d => d.Start))
        {
            if (d.Start > pos)
            {
                segments.Add(new TextSegment(Text[pos..d.Start], SegmentKind.Plain));
            }

            segments.Add(new TextSegment(d.Text, KindOf(d)));
            pos = d.End;
        }

        if (pos < Text.Length)
        {
            segments.Add(new TextSegment(Text[pos..], SegmentKind.Plain));
        }

        return segments;
    }

    private List<TextSegment> BuildAnonymizedSegments()
    {
        var segments = new List<TextSegment>();
        var pos = 0;
        foreach (var d in _detections.OrderBy(d => d.Start))
        {
            if (d.Start > pos)
            {
                segments.Add(new TextSegment(Text[pos..d.Start], SegmentKind.Plain));
            }

            segments.Add(
                d.IsReplaced
                    ? new TextSegment(
                        d.Placeholder!,
                        KindOf(d) == SegmentKind.Review
                            ? SegmentKind.Review
                            : SegmentKind.Placeholder
                    )
                    : new TextSegment(d.Text, SegmentKind.Rejected)
            );
            pos = d.End;
        }

        if (pos < Text.Length)
        {
            segments.Add(new TextSegment(Text[pos..], SegmentKind.Plain));
        }

        return segments;
    }

    internal void SetStatus(DetectionGroupViewModel group, ReviewStatus status)
    {
        foreach (var d in group.Detections)
        {
            d.Status = status;
        }

        group.Refresh();
        Refresh();
    }

    internal void ChangeCategory(DetectionGroupViewModel group, Category category)
    {
        if (_working is null)
        {
            return;
        }

        foreach (var d in group.Detections)
        {
            d.Category = category;
            d.Placeholder = null;
            d.EntityId = null;
            d.Status = ReviewStatus.Approved;
        }

        Anonymizer.AssignPlaceholders(Text, group.Detections, _working);
        Rebuild();
    }

    [RelayCommand]
    private void ApproveAll()
    {
        foreach (var d in _detections.Where(d => d.Status == ReviewStatus.Pending))
        {
            d.Status = ReviewStatus.Approved;
        }

        Rebuild();
    }

    [RelayCommand]
    private void AnonymizeSelection()
    {
        if (_working is null)
        {
            return;
        }

        var start = Math.Min(SelectionStart, SelectionEnd);
        var end = Math.Max(SelectionStart, SelectionEnd);
        if (end <= start || end > Text.Length)
        {
            Message = "Select some text in the original (left) first.";
            return;
        }

        _detections = Anonymizer.AddManual(
            Text,
            _detections,
            start,
            end - start,
            ManualCategory,
            _working
        );
        Message = null;
        Rebuild();
    }

    private bool CanCopy() => HasDocument && PendingCount == 0 && Confirmed;

    /// <summary>
    /// Export: only the reviewed anonymized text leaves the application. The mappings are
    /// committed to the encrypted case dictionary.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        var current = workspace.CurrentCase;
        if (_working is null || current is null)
        {
            return;
        }

        foreach (
            var d in _detections.Where(d =>
                d.Status == ReviewStatus.Rejected && d.Source != DetectionSource.Manual
            )
        )
        {
            _working.Ignore(d.Text);
        }

        var used = _detections
            .Where(d => d.IsReplaced && d.EntityId is not null)
            .Select(d => d.EntityId!)
            .ToHashSet();
        _working.Prune(_preexisting, used);

        current.Dictionary = _working.Clone();
        current.DocumentCount++;
        workspace.SaveCurrentCase();
        workspace.Entities.Reload();
        _preexisting = _working.Entities.Select(e => e.Id).ToHashSet();

        await workspace.Ui.SetClipboardTextAsync(AnonymizedText);
        Message = "Anonymized text copied. The mappings were saved to the encrypted case.";
    }
}
