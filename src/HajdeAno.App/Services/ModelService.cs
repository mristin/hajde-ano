using HajdeAno.Core.Llm;
using HajdeAno.Core.Storage;
using HajdeAno.Llm;

namespace HajdeAno.App.Services;

/// <summary>
/// Owns the single loaded model. Only one model is loaded at a time, and inference runs one
/// operation at a time (enforced by the extractor).
/// </summary>
public sealed class ModelService : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private LlamaEntityExtractor? _extractor;
    private string? _loadedKey;

    public string? LoadedModelPath { get; private set; }

    public bool IsLoaded => _extractor is not null;

    private static string KeyOf(AppSettings s, string path) =>
        $"{path}|{s.ContextSize}|{s.Device}|{s.GpuLayerCount}|{s.Threads}";

    /// <summary>
    /// Get the extractor for the current settings, loading (or reloading) the model if
    /// needed. Throws <see cref="FileNotFoundException"/> if no model is available locally.
    /// </summary>
    public async Task<IEntityExtractor> GetAsync(AppSettings settings, CancellationToken ct)
    {
        var path = AppPaths.ResolveModel(settings.ModelPath)
            ?? throw new FileNotFoundException(
                "No local model found. Select a GGUF file in Settings. "
                    + "The application never downloads models by itself.",
                settings.ModelPath
            );

        await _lock.WaitAsync(ct);
        try
        {
            var key = KeyOf(settings, path);
            if (_extractor is not null && _loadedKey == key)
            {
                return _extractor;
            }

            // Never keep two models in memory.
            _extractor?.Dispose();
            _extractor = null;
            LoadedModelPath = null;

            _extractor = await LlamaEntityExtractor.LoadAsync(
                new LlamaOptions
                {
                    ModelPath = path,
                    ContextSize = (uint)settings.ContextSize,
                    GpuLayerCount =
                        settings.Device == InferenceDevice.Gpu ? settings.GpuLayerCount : 0,
                    Threads = settings.Threads > 0 ? settings.Threads : null,
                },
                ct
            );
            _loadedKey = key;
            LoadedModelPath = path;
            return _extractor;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Unload()
    {
        _extractor?.Dispose();
        _extractor = null;
        _loadedKey = null;
        LoadedModelPath = null;
    }

    public void Dispose()
    {
        Unload();
        _lock.Dispose();
    }
}
