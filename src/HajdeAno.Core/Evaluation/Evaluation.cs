using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HajdeAno.Core.Llm;

namespace HajdeAno.Core;

/// <summary>An annotated synthetic entity.</summary>
public sealed record ExpectedEntity(string Text, string Type);

/// <summary>A synthetic, annotated test case (fictional data only).</summary>
public sealed class SyntheticCase
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public List<string> Documents { get; set; } = [];

    public List<ExpectedEntity> Expected { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SyntheticCase FromJson(string json) =>
        JsonSerializer.Deserialize<SyntheticCase>(json, JsonOptions)
        ?? throw new InvalidDataException("Invalid synthetic case");

    public static List<SyntheticCase> LoadAll(string directory) =>
        Directory.Exists(directory)
            ? Directory
                .EnumerateFiles(directory, "*.json")
                .Order(StringComparer.Ordinal)
                .Select(p => FromJson(File.ReadAllText(p)))
                .ToList()
            : [];
}

/// <summary>Per-category recall counts.</summary>
public sealed class RecallCounter
{
    public int Total { get; set; }

    public int Covered { get; set; }

    public double Recall => Total == 0 ? 1.0 : (double)Covered / Total;
}

/// <summary>Detection-quality evaluation on synthetic cases.</summary>
public static class Evaluation
{
    /// <summary>
    /// An expected entity counts as covered in a document if every occurrence of it is
    /// fully inside a replaced detection.
    /// </summary>
    public static bool IsCovered(string text, string entity, IReadOnlyList<Detection> detections)
    {
        var occurrences = TextUtil.FindOccurrences(text, entity, ignoreCase: false).ToList();
        if (occurrences.Count == 0)
        {
            return true;
        }

        return occurrences.All(o =>
            detections.Any(d => d.IsReplaced && d.Start <= o.Start && o.Start + o.Length <= d.End)
        );
    }

    public static async Task<string> RunAsync(
        IReadOnlyList<SyntheticCase> cases,
        IEntityExtractor? extractor,
        AnonymizationOptions options,
        string modelName
    )
    {
        var perCategory = new SortedDictionary<string, RecallCounter>(StringComparer.Ordinal);
        var missed = new List<string>();
        var sw = Stopwatch.StartNew();
        var documents = 0;
        var characters = 0;
        var invalidJson = 0;
        var hallucinated = 0;

        foreach (var c in cases)
        {
            var dictionary = new CaseDictionary();
            var anonymizer = new Anonymizer(extractor, options);
            foreach (var doc in c.Documents)
            {
                documents++;
                characters += doc.Length;
                var result = await anonymizer
                    .AnalyzeAsync(doc, dictionary, null, CancellationToken.None)
                    .ConfigureAwait(false);
                invalidJson += result.FirstPassStats?.InvalidJsonChunks ?? 0;
                hallucinated += result.FirstPassStats?.HallucinatedEntities ?? 0;

                foreach (var e in c.Expected)
                {
                    if (TextUtil.FindOccurrences(doc, e.Text, ignoreCase: false).Any() is false)
                    {
                        continue;
                    }

                    var counter = perCategory.TryGetValue(e.Type, out var existing)
                        ? existing
                        : perCategory[e.Type] = new RecallCounter();
                    counter.Total++;
                    if (IsCovered(doc, e.Text, result.Detections))
                    {
                        counter.Covered++;
                    }
                    else
                    {
                        missed.Add($"{c.Name}: {e.Type} \"{e.Text}\"");
                    }
                }
            }
        }

        sw.Stop();
        using var process = Process.GetCurrentProcess();
        var total = new RecallCounter
        {
            Total = perCategory.Values.Sum(v => v.Total),
            Covered = perCategory.Values.Sum(v => v.Covered),
        };

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"## Evaluation: {modelName}");
        sb.AppendLine();
        sb.AppendLine(
            inv,
            $"- Cases: {cases.Count}, documents: {documents}, characters: {characters}"
        );
        sb.AppendLine(inv, $"- Second pass: {(options.SecondPass ? "on" : "off")}");
        sb.AppendLine(inv, $"- Total time: {sw.Elapsed.TotalSeconds:F1} s");
        sb.AppendLine(
            inv,
            $"- Peak working set: {process.PeakWorkingSet64 / (1024.0 * 1024.0):F0} MiB"
        );
        sb.AppendLine(inv, $"- Chunks with invalid JSON: {invalidJson}");
        sb.AppendLine(inv, $"- Hallucinated spans discarded: {hallucinated}");
        sb.AppendLine(
            inv,
            $"- **Overall recall: {total.Recall:P1}** ({total.Covered}/{total.Total})"
        );
        sb.AppendLine();
        sb.AppendLine("| Category | Recall | Covered / Total |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var (category, counter) in perCategory)
        {
            sb.AppendLine(
                inv,
                $"| {category} | {counter.Recall:P1} | {counter.Covered} / {counter.Total} |"
            );
        }

        if (missed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Missed (synthetic data):");
            sb.AppendLine();
            foreach (var m in missed)
            {
                sb.AppendLine(inv, $"- {m}");
            }
        }

        return sb.ToString();
    }
}
