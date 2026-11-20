using System.Text.Json;
using System.Text.Json.Serialization;

namespace HajdeAno.Core.Storage;

/// <summary>Inference device.</summary>
public enum InferenceDevice
{
    Cpu,
    Gpu,
}

/// <summary>Application settings, stored as JSON in the data directory.</summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";

    /// <summary>Path to the GGUF model; if empty, the default locations are searched.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>Defaults to the GPU (Metal) on Apple Silicon and to the CPU elsewhere.</summary>
    public InferenceDevice Device { get; set; } =
        OperatingSystem.IsMacOS()
        && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
            ? InferenceDevice.Gpu
            : InferenceDevice.Cpu;

    /// <summary>Layers offloaded to the GPU when <see cref="Device"/> is GPU (Metal on Apple Silicon).</summary>
    public int GpuLayerCount { get; set; } = 99;

    public int ContextSize { get; set; } = 4096;

    /// <summary>CPU threads; 0 means automatic.</summary>
    public int Threads { get; set; }

    public bool StrictMode { get; set; } = true;

    public bool SecondPass { get; set; } = true;

    /// <summary>Disabled categories (all others are enabled).</summary>
    public List<Category> DisabledCategories { get; set; } = [];

    /// <summary>Most recently opened session folders, most recent first.</summary>
    public List<string> RecentSessions { get; set; } = [];

    public const int MaxRecentSessions = 15;

    /// <summary>Put <paramref name="directory"/> at the top of the recent sessions.</summary>
    public void AddRecentSession(string directory)
    {
        var full = Path.GetFullPath(directory);
        RemoveRecentSession(full);
        RecentSessions.Insert(0, full);
        if (RecentSessions.Count > MaxRecentSessions)
        {
            RecentSessions.RemoveRange(MaxRecentSessions, RecentSessions.Count - MaxRecentSessions);
        }
    }

    public void RemoveRecentSession(string directory)
    {
        var full = Path.GetFullPath(directory);
        RecentSessions.RemoveAll(d => string.Equals(d, full, StringComparison.Ordinal));
    }

    public AnonymizationOptions ToOptions() =>
        new()
        {
            StrictMode = StrictMode,
            SecondPass = SecondPass,
            EnabledCategories = new HashSet<Category>(
                Categories.All.Where(c => !DisabledCategories.Contains(c))
            ),
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    public static AppSettings Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, FileName),
            JsonSerializer.Serialize(this, JsonOptions)
        );
    }
}

/// <summary>Well-known local paths.</summary>
public static class AppPaths
{
    public const string DataDirEnvVar = "HAJDE_ANO_DATA_DIR";

    /// <summary>Directory for the settings and the list of recent sessions (overridable for tests).</summary>
    public static string DataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden;
        }

        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create
            ),
            "HajdeAno"
        );
    }

    /// <summary>Default model file names in order of preference.</summary>
    public static IReadOnlyList<string> DefaultModelFileNames { get; } =
    ["Qwen3-8B-Q4_K_M.gguf", "Qwen3-8B-Q5_K_M.gguf", "Qwen3-4B-Q4_K_M.gguf"];

    /// <summary>Directories that are searched for models.</summary>
    public static IEnumerable<string> ModelDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "models");

        // macOS bundle: Contents/MacOS/../Resources/models
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "models"));
        yield return Path.Combine(DataDirectory(), "models");
        yield return Path.Combine(Environment.CurrentDirectory, "models");
    }

    /// <summary>All GGUF files in the model directories.</summary>
    public static IEnumerable<string> FindModels() =>
        ModelDirectories()
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.gguf"))
            .Select(Path.GetFullPath)
            .Distinct();

    /// <summary>Resolve the model: the configured path, else the preferred default found locally.</summary>
    public static string? ResolveModel(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        var found = FindModels().ToList();
        foreach (var name in DefaultModelFileNames)
        {
            var match = found.Find(f => Path.GetFileName(f) == name);
            if (match is not null)
            {
                return match;
            }
        }

        return found.FirstOrDefault();
    }
}
