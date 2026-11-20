using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HajdeAno.Core;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.ViewModels;

public sealed partial class CategoryToggleViewModel(Category category, bool enabled) : ViewModelBase
{
    public Category Category { get; } = category;

    public string Name => Category.DisplayName() + (Category.IsIndirect() ? " (strict mode)" : "");

    [ObservableProperty]
    private bool _enabled = enabled;
}

/// <summary>Screen 5: settings.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly WorkspaceViewModel _workspace;

    public SettingsViewModel(WorkspaceViewModel workspace)
    {
        _workspace = workspace;
        var s = workspace.Services.Settings;
        _modelPath = s.ModelPath;
        _useGpu = s.Device == InferenceDevice.Gpu;
        _contextSize = s.ContextSize;
        _threads = s.Threads;
        _strictMode = s.StrictMode;
        _secondPass = s.SecondPass;
        foreach (var c in Categories.All)
        {
            CategoryToggles.Add(new CategoryToggleViewModel(c, !s.DisabledCategories.Contains(c)));
        }

        RefreshModels();
    }

    public ObservableCollection<string> FoundModels { get; } = [];

    public ObservableCollection<CategoryToggleViewModel> CategoryToggles { get; } = [];

    public IReadOnlyList<int> ContextSizes { get; } = [4096, 8192];

    [ObservableProperty]
    private string _modelPath;

    [ObservableProperty]
    private bool _useGpu;

    [ObservableProperty]
    private int _contextSize;

    [ObservableProperty]
    private int _threads;

    [ObservableProperty]
    private bool _strictMode;

    [ObservableProperty]
    private bool _secondPass;

    [ObservableProperty]
    private string? _modelStatus;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    public string DataDirectory => _workspace.Services.DataDirectory;

    public string SessionDirectory => _workspace.Services.Session?.Directory ?? "";

    public string ModelDirectories => string.Join("\n", AppPaths.ModelDirectories().Distinct());

    private void RefreshModels()
    {
        FoundModels.Clear();
        foreach (var m in AppPaths.FindModels())
        {
            FoundModels.Add(m);
        }

        var resolved = AppPaths.ResolveModel(ModelPath);
        ModelStatus = resolved is null
            ? "No model found. Download a GGUF model (see README) and select it here."
            : $"Will use: {resolved}";
    }

    partial void OnModelPathChanged(string value) => RefreshModels();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _workspace.Ui.PickModelFileAsync();
        if (path is not null)
        {
            ModelPath = path;
        }
    }

    [RelayCommand]
    private void UseFound(string path) => ModelPath = path;

    [RelayCommand]
    private void Save()
    {
        var s = _workspace.Services.Settings;
        s.ModelPath = ModelPath.Trim();
        s.Device = UseGpu ? InferenceDevice.Gpu : InferenceDevice.Cpu;
        s.ContextSize = ContextSize;
        s.Threads = Math.Max(0, Threads);
        s.StrictMode = StrictMode;
        s.SecondPass = SecondPass;
        s.DisabledCategories = CategoryToggles
            .Where(t => !t.Enabled)
            .Select(t => t.Category)
            .ToList();
        _workspace.Services.SaveSettings();
        _workspace.Input.RefreshModelInfo();
        RefreshModels();
        Message = "Settings saved. The model is (re)loaded on the next detection.";
    }

    [RelayCommand]
    private async Task TestModelAsync()
    {
        Save();
        IsBusy = true;
        Message = "Loading the model…";
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _workspace.Services.Models.GetAsync(
                _workspace.Services.Settings,
                null,
                CancellationToken.None
            );
            Message = $"Model loaded in {sw.Elapsed.TotalSeconds:F1} s.";
        }
        catch (FileNotFoundException e)
        {
            Message = e.Message;
        }
        catch (Exception e)
            when (e is InvalidOperationException or IOException or LLama.Exceptions.RuntimeError)
        {
            Message = $"Could not load the model ({e.GetType().Name}).";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
