using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HajdeAno.Core;
using HajdeAno.Core.Llm;
using HajdeAno.Core.Storage;
using HajdeAno.Llm;

namespace HajdeAno.Cli;

/// <summary>
/// Developer command-line interface: run extraction on synthetic texts, anonymize, restore
/// and evaluate detection quality. Never use it with real private case descriptions in a
/// context where its output could leave your machine.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage: hajde-ano-cli <command> [options]

        Commands:
          extract   --model PATH [--ctx N] [--gpu-layers N] [--threads N] [--raw] FILE
                    Run the local LLM on FILE and print the validated entities as JSON.
          anonymize [--model PATH] [--dictionary IN.dict] [--save-dictionary OUT.dict]
                    [--lenient] [--no-second-pass] FILE
                    Print the anonymized text (without a model: patterns + dictionary only).
          restore   --dictionary DICT.dict FILE
                    Restore the placeholders in FILE.

        Dictionary files map names to placeholders and are therefore always encrypted
        (AES-256-GCM, key derived from a password which is asked for on the terminal, or
        read from the first line of standard input if it is redirected).
          evaluate  [--model PATH] [--data DIR] [--report OUT.md] [--no-second-pass]
                    Measure recall on the annotated synthetic cases.

        Common options:
          --ctx N          Context size in tokens (default 4096)
          --gpu-layers N   Layers to offload to the GPU (default 0, CPU only)
          --threads N      CPU threads (default: automatic)
          --prompt-batch N Prompt tokens decoded per step (default 64, at most 512)
          --no-template    Do not wrap the prompt in the Qwen3 chat template (toy models)
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var options = Options.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "extract" => await ExtractAsync(options),
                "anonymize" => await AnonymizeAsync(options),
                "restore" => Restore(options),
                "evaluate" => await EvaluateAsync(options),
                _ => Fail($"Unknown command: {args[0]}\n\n{Usage}"),
            };
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
        catch (FileNotFoundException e)
        {
            return Fail($"{e.Message} ({e.FileName})");
        }
        catch (UnauthorizedAccessException e)
        {
            return Fail(e.Message);
        }
        catch (InvalidDataException e)
        {
            return Fail(e.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private sealed class Options
    {
        public Dictionary<string, string> Values { get; } = [];

        public HashSet<string> Flags { get; } = [];

        public List<string> Positional { get; } = [];

        private static readonly HashSet<string> FlagNames =
        [
            "--raw",
            "--lenient",
            "--no-second-pass",
            "--no-template",
        ];

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (FlagNames.Contains(a))
                {
                    o.Flags.Add(a);
                }
                else if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"Missing value for {a}");
                    }

                    o.Values[a] = args[++i];
                }
                else
                {
                    o.Positional.Add(a);
                }
            }

            return o;
        }

        public string? Get(string name) => Values.GetValueOrDefault(name);

        public int GetInt(string name, int fallback) =>
            Values.TryGetValue(name, out var v)
                ? int.Parse(v, CultureInfo.InvariantCulture)
                : fallback;

        public string File(int index = 0) =>
            index < Positional.Count
                ? Positional[index]
                : throw new ArgumentException("Missing input file");
    }

    private static async Task<LlamaEntityExtractor?> LoadModelAsync(Options o, bool required)
    {
        var path = o.Get("--model");
        if (path is null)
        {
            if (required)
            {
                throw new ArgumentException("--model is required");
            }

            return null;
        }

        var threads = o.GetInt("--threads", 0);
        var sw = Stopwatch.StartNew();
        var extractor = await LlamaEntityExtractor.LoadAsync(
            new LlamaOptions
            {
                ModelPath = path,
                ContextSize = (uint)o.GetInt("--ctx", 4096),
                GpuLayerCount = o.GetInt("--gpu-layers", 0),
                Threads = threads > 0 ? threads : null,
                UseChatTemplate = !o.Flags.Contains("--no-template"),
                PromptBatchSize = o.GetInt("--prompt-batch", 64),
            }
        );
        Console.Error.WriteLine($"Model loaded in {sw.Elapsed.TotalSeconds:F1} s");
        return extractor;
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task<int> ExtractAsync(Options o)
    {
        var text = await File.ReadAllTextAsync(o.File());
        using var extractor = (await LoadModelAsync(o, required: true))!;

        if (o.Flags.Contains("--raw"))
        {
            var sw0 = Stopwatch.StartNew();
            var raw = await extractor.ExtractRawAsync(text, null, CancellationToken.None);
            Console.WriteLine(raw);
            Console.Error.WriteLine($"Inference: {sw0.Elapsed.TotalSeconds:F1} s");
            return 0;
        }

        var detector = new LlmDetector(extractor);
        var (detections, stats) = await detector.DetectAsync(
            text,
            DetectionSource.Llm,
            null,
            CancellationToken.None
        );
        var entities = detections
            .GroupBy(d => (d.Text, d.Category))
            .Select(g => new
            {
                text = g.Key.Text,
                type = g.Key.Category.LlmLabel(),
                occurrences = g.Count(),
            })
            .ToList();
        Console.WriteLine(JsonSerializer.Serialize(entities, PrettyJson));
        PrintStats(stats);
        return 0;
    }

    private static void PrintStats(LlmRunStats stats)
    {
        using var process = Process.GetCurrentProcess();
        Console.Error.WriteLine(
            $"Chunks: {stats.Chunks}, invalid JSON: {stats.InvalidJsonChunks}, "
                + $"reported: {stats.ReportedEntities}, hallucinated: {stats.HallucinatedEntities}, "
                + $"inference: {stats.Elapsed.TotalSeconds:F1} s, "
                + $"peak working set: {process.PeakWorkingSet64 / (1024.0 * 1024.0):F0} MiB"
        );
    }

    private static async Task<int> AnonymizeAsync(Options o)
    {
        var text = await File.ReadAllTextAsync(o.File());
        using var extractor = await LoadModelAsync(o, required: false);
        var dictionary = o.Get("--dictionary") is { } dictPath
            ? ReadDictionary(dictPath)
            : new CaseDictionary();

        var anonymizer = new Anonymizer(
            extractor,
            new AnonymizationOptions
            {
                StrictMode = !o.Flags.Contains("--lenient"),
                SecondPass = !o.Flags.Contains("--no-second-pass"),
            }
        );
        var result = await anonymizer.AnalyzeAsync(text, dictionary, null, CancellationToken.None);
        Console.WriteLine(result.AnonymizedText);
        foreach (var w in result.Warnings)
        {
            Console.Error.WriteLine($"Warning: {w}");
        }

        var review = result.Detections.Count(d => d.NeedsReview);
        Console.Error.WriteLine(
            $"{result.Detections.Count} detection(s), {review} need(s) review."
        );

        if (o.Get("--save-dictionary") is { } outPath)
        {
            WriteDictionary(outPath, dictionary);
        }

        return 0;
    }

    private static int Restore(Options o)
    {
        var dictPath = o.Get("--dictionary")
            ?? throw new ArgumentException("--dictionary is required");
        var dictionary = ReadDictionary(dictPath);
        var result = Restorer.Restore(File.ReadAllText(o.File()), dictionary);
        Console.WriteLine(result.Text);
        foreach (var issue in result.Issues)
        {
            Console.Error.WriteLine($"Warning: {issue.Kind} placeholder: {issue.Text}");
        }

        return result.Issues.Count == 0 ? 0 : 2;
    }

    /// <summary>Read a password from the terminal without echoing it.</summary>
    private static string ReadPassword(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine();
            return Console.In.ReadLine() ?? "";
        }

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
    }

    private static CaseDictionary ReadDictionary(string path)
    {
        var data = File.ReadAllBytes(path);
        if (!Crypto.IsPasswordEncrypted(data))
        {
            throw new InvalidDataException(
                $"{path} is not an encrypted dictionary. Dictionaries are never stored unencrypted."
            );
        }

        var plaintext = Crypto.DecryptWithPassword(
            ReadPassword("Password of the dictionary: "),
            data
        );
        try
        {
            return CaseDictionary.FromJson(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void WriteDictionary(string path, CaseDictionary dictionary)
    {
        var password = ReadPassword("Password to encrypt the dictionary: ");
        if (password.Length < 8)
        {
            throw new ArgumentException("The password must have at least 8 characters");
        }

        if (!Console.IsInputRedirected && ReadPassword("Repeat the password: ") != password)
        {
            throw new ArgumentException("The passwords do not match");
        }

        var plaintext = Encoding.UTF8.GetBytes(dictionary.ToJson());
        try
        {
            var data = Crypto.EncryptWithPassword(password, plaintext);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static async Task<int> EvaluateAsync(Options o)
    {
        var dataDir = o.Get("--data") ?? Path.Combine("tests", "data", "cases");
        var cases = SyntheticCase.LoadAll(dataDir);
        if (cases.Count == 0)
        {
            return Fail($"No synthetic cases found in {dataDir}");
        }

        using var extractor = await LoadModelAsync(o, required: false);
        var report = await Evaluation.RunAsync(
            cases,
            extractor,
            new AnonymizationOptions { SecondPass = !o.Flags.Contains("--no-second-pass") },
            o.Get("--model") is { } m ? Path.GetFileName(m) : "(no model: patterns only)"
        );

        Console.WriteLine(report);
        if (o.Get("--report") is { } reportPath)
        {
            await File.WriteAllTextAsync(reportPath, report);
        }

        return 0;
    }
}
