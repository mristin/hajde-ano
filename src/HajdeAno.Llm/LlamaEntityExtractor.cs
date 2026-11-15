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

    /// <summary>
    /// Prompt tokens decoded per step. Larger steps are more efficient; progress is
    /// reported after each step.
    /// </summary>
    public int PromptBatchSize { get; init; } = 64;

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
    private readonly List<LLamaToken> _cachedTokens = [];
    private LLamaContext? _context;

    private LlamaEntityExtractor(LlamaOptions options, LLamaWeights weights, ModelParams parameters)
    {
        _options = options;
        _weights = weights;
        _params = parameters;
    }

    /// <summary>Path of the loaded model.</summary>
    public string ModelPath => _options.ModelPath;

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
        IProgress<float>? loadProgress = null,
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
            .LoadFromFileAsync(parameters, cancellationToken, loadProgress)
            .ConfigureAwait(false);
        return new LlamaEntityExtractor(options, weights, parameters);
    }

    /// <summary>Build the prompt for the text.</summary>
    public string BuildPrompt(string text) =>
        _options.UseChatTemplate
            ? LlmPrompt.BuildChatMlPrompt(text)
            : LlmPrompt.SystemPrompt + "\n\nText:\n" + text + "\n\nJSON:\n";

    /// <summary>
    /// Run the model on the text. The prompt is decoded in small batches (so that progress
    /// can be reported while the text is read), and the KV cache of the previous call is
    /// reused for the common prefix (the fixed system prompt and example), so that only
    /// the new text has to be processed.
    /// </summary>
    public async Task<string> ExtractRawAsync(
        string text,
        IProgress<InferenceProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prompt = BuildPrompt(text);
            _context ??= _weights.CreateContext(_params);
            var context = _context;
            var tokens = context.Tokenize(prompt, true, true);
            var budget = (int)_options.ContextSize - tokens.Length - 8;
            if (budget < 64)
            {
                throw new InvalidOperationException(
                    $"The chunk does not fit into the context ({tokens.Length} prompt tokens, context {_options.ContextSize}). Increase the context size."
                );
            }

            // Reuse the cached prefix; at least one token must be decoded to get logits.
            var keep = 0;
            while (
                keep < _cachedTokens.Count
                && keep < tokens.Length - 1
                && _cachedTokens[keep] == tokens[keep]
            )
            {
                keep++;
            }

            context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, keep, -1);
            _cachedTokens.RemoveRange(keep, _cachedTokens.Count - keep);

            progress?.Report(new InferenceProgress(tokens.Length, keep, 0));
            var promptBatch = Math.Clamp(_options.PromptBatchSize, 1, (int)_params.BatchSize);
            var batch = new LLamaBatch();
            for (var start = keep; start < tokens.Length; start += promptBatch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(tokens.Length, start + promptBatch);
                batch.Clear();
                for (var i = start; i < end; i++)
                {
                    batch.Add(tokens[i], i, LLamaSeqId.Zero, i == tokens.Length - 1);
                }

                await DecodeOrThrowAsync(context, batch, cancellationToken).ConfigureAwait(false);
                for (var i = start; i < end; i++)
                {
                    _cachedTokens.Add(tokens[i]);
                }

                progress?.Report(new InferenceProgress(tokens.Length, end, 0));
            }

            using var sampling = new DefaultSamplingPipeline
            {
                Temperature = 0f,
                Grammar = new Grammar(LlmPrompt.Grammar, "root"),
                Seed = 42,
            };
            var decoder = new StreamingTokenDecoder(context);
            var sb = new StringBuilder();
            var maxTokens = Math.Min(budget, _options.MaxOutputTokens);
            var position = tokens.Length;
            var logitsIndex = batch.TokenCount - 1;
            for (var generated = 1; generated <= maxTokens; generated++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = sampling.Sample(context.NativeHandle, logitsIndex);
                if (token.IsEndOfGeneration(context.Vocab))
                {
                    break;
                }

                decoder.Add(token);
                sb.Append(decoder.Read());
                progress?.Report(new InferenceProgress(tokens.Length, tokens.Length, generated));

                batch.Clear();
                batch.Add(token, position++, LLamaSeqId.Zero, true);
                logitsIndex = 0;
                await DecodeOrThrowAsync(context, batch, cancellationToken).ConfigureAwait(false);
            }

            // Only the prompt is a reusable prefix; the generated part is dropped next time.
            return sb.ToString().Replace("<|im_end|>", "", StringComparison.Ordinal);
        }
        catch
        {
            // After an error or cancellation the cache state is unknown: start afresh.
            _cachedTokens.Clear();
            _context?.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, 0, -1);
            throw;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private static async Task DecodeOrThrowAsync(
        LLamaContext context,
        LLamaBatch batch,
        CancellationToken cancellationToken
    )
    {
        var result = await context.DecodeAsync(batch, cancellationToken).ConfigureAwait(false);
        if (result != DecodeResult.Ok)
        {
            throw new InvalidOperationException($"Decoding failed: {result}");
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _weights.Dispose();
        _oneAtATime.Dispose();
    }
}
