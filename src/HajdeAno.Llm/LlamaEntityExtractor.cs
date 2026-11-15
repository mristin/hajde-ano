using System.Text;
using HajdeAno.Core.Llm;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace HajdeAno.Llm;

/// <summary>Configuration of the local model.</summary>
public sealed record LlamaOptions
{
    public required string ModelPath { get; init; }

    public uint ContextSize { get; init; } = 4096;

    /// <summary>0 for pure CPU inference; e.g. 99 to offload everything (Metal on Apple Silicon).</summary>
    public int GpuLayerCount { get; init; }

    /// <summary>CPU threads; null lets llama.cpp decide.</summary>
    public int? Threads { get; init; }

    /// <summary>Upper bound on generated tokens per chunk.</summary>
    public int MaxOutputTokens { get; init; } = 1536;

    /// <summary>Wrap the prompt in the Qwen3 ChatML template (disable for toy test models).</summary>
    public bool UseChatTemplate { get; init; } = true;
}

/// <summary>
/// Entity extraction with a local GGUF model through LLamaSharp (llama.cpp in-process).
/// No network access, no external processes, no server.
/// </summary>
public sealed class LlamaEntityExtractor : IEntityExtractor, IDisposable
{
    private static readonly object NativeInitLock = new();
    private static bool _nativeInitialized;

    private readonly LlamaOptions _options;
    private readonly LLamaWeights _weights;
    private readonly ModelParams _params;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private LlamaEntityExtractor(LlamaOptions options, LLamaWeights weights, ModelParams parameters)
    {
        _options = options;
        _weights = weights;
        _params = parameters;
    }

    /// <summary>Approximate characters per chunk derived from the context size.</summary>
    public int MaxChunkChars =>
        Math.Max(600, (int)Math.Min(6000, (_options.ContextSize - 900) * 1.1));

    /// <summary>
    /// Configure the native library once: silence llama.cpp logging (it must never print
    /// prompts) and search next to the executable and in a macOS bundle.
    /// </summary>
    public static void InitializeNative()
    {
        lock (NativeInitLock)
        {
            if (_nativeInitialized)
            {
                return;
            }

            var baseDir = AppContext.BaseDirectory;
            var config = NativeLibraryConfig.All.WithLogCallback(static (_, _) => { });
            config.WithSearchDirectories([
                baseDir,
                Path.Combine(baseDir, "native"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks")),
            ]);
            _nativeInitialized = true;
        }
    }

    /// <summary>Load the model. Throws <see cref="FileNotFoundException"/> if it is missing.</summary>
    public static async Task<LlamaEntityExtractor> LoadAsync(
        LlamaOptions options,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException(
                "The model file does not exist. Models are never downloaded automatically.",
                options.ModelPath
            );
        }

        InitializeNative();
        NativeLogConfig.llama_log_set(static (_, _) => { });

        var parameters = new ModelParams(options.ModelPath)
        {
            ContextSize = options.ContextSize,
            GpuLayerCount = options.GpuLayerCount,
            Threads = options.Threads,
            BatchSize = 512,
        };
        var weights = await LLamaWeights
            .LoadFromFileAsync(parameters, cancellationToken)
            .ConfigureAwait(false);
        return new LlamaEntityExtractor(options, weights, parameters);
    }

    /// <summary>Build the prompt for the text.</summary>
    public string BuildPrompt(string text) =>
        _options.UseChatTemplate
            ? LlmPrompt.BuildChatMlPrompt(text)
            : LlmPrompt.SystemPrompt + "\n\nText:\n" + text + "\n\nJSON:\n";

    public async Task<string> ExtractRawAsync(string text, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prompt = BuildPrompt(text);
            var promptTokens = _weights.Tokenize(prompt, true, true, Encoding.UTF8).Length;
            var budget = (int)_options.ContextSize - promptTokens - 8;
            if (budget < 64)
            {
                throw new InvalidOperationException(
                    $"The chunk does not fit into the context ({promptTokens} prompt tokens, context {_options.ContextSize}). Increase the context size."
                );
            }

            using var sampling = new DefaultSamplingPipeline
            {
                Temperature = 0f,
                Grammar = new Grammar(LlmPrompt.Grammar, "root"),
                Seed = 42,
            };
            var inference = new InferenceParams
            {
                MaxTokens = Math.Min(budget, _options.MaxOutputTokens),
                AntiPrompts = ["<|im_end|>"],
                SamplingPipeline = sampling,
            };

            var executor = new StatelessExecutor(_weights, _params) { ApplyTemplate = false };
            var sb = new StringBuilder();
            await foreach (
                var piece in executor
                    .InferAsync(prompt, inference, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                sb.Append(piece);
            }

            return sb.ToString().Replace("<|im_end|>", "", StringComparison.Ordinal);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public void Dispose()
    {
        _weights.Dispose();
        _oneAtATime.Dispose();
    }
}
