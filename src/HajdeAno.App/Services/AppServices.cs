using HajdeAno.Core.Llm;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.Services;

/// <summary>Shared state of the running application.</summary>
public sealed class AppServices(string dataDirectory) : IDisposable
{
    public string DataDirectory { get; } = dataDirectory;

    public AppSettings Settings { get; private set; } = AppSettings.Load(dataDirectory);

    public ModelService Models { get; } = new();

    /// <summary>Optional extractor override (used for screenshots and tests).</summary>
    public IEntityExtractor? ExtractorOverride { get; set; }

    public Vault? Vault { get; set; }

    public void SaveSettings() => Settings.Save(DataDirectory);

    public void Dispose()
    {
        Vault?.Dispose();
        Models.Dispose();
    }
}

/// <summary>Abstraction over clipboard and file dialogs so view models stay testable.</summary>
public interface IUiServices
{
    Task SetClipboardTextAsync(string text);

    Task<string?> PickTextFileAsync();

    Task<string?> PickModelFileAsync();
}
