namespace HajdeAno.Core;

/// <summary>Merge detections from multiple sources into a set of non-overlapping spans.</summary>
public static class SpanMerger
{
    private static int SourcePriority(DetectionSource source) =>
        source switch
        {
            DetectionSource.Manual => 0,
            DetectionSource.Dictionary => 1,
            DetectionSource.Llm => 2,
            DetectionSource.Regex => 3,
            DetectionSource.Fuzzy => 4,
            DetectionSource.SecondPass => 5,
            _ => 6,
        };

    /// <summary>
    /// Resolve overlaps. Manual detections always win; otherwise the longest span wins
    /// (so that e.g. a full address beats the postal code inside it), and ties are broken
    /// by source priority.
    /// </summary>
    /// <remarks>
    /// When a weaker detection overlaps a stronger one, but is not fully contained in it,
    /// the stronger span is extended to cover both. This favors recall: partially
    /// overlapping spans never leak the non-overlapping part.
    /// </remarks>
    public static List<Detection> Merge(string text, IEnumerable<Detection> detections)
    {
        var ordered = detections
            .Where(d => d.End <= text.Length)
            .OrderBy(d => d.Source == DetectionSource.Manual ? 0 : 1)
            .ThenByDescending(d => d.Length)
            .ThenBy(d => SourcePriority(d.Source))
            .ThenBy(d => d.Start)
            .ToList();

        var accepted = new List<Detection>();
        foreach (var candidate in ordered)
        {
            var overlapping = accepted.Where(a => a.Overlaps(candidate)).ToList();
            if (overlapping.Count == 0)
            {
                accepted.Add(candidate);
                continue;
            }

            // Fully contained in an accepted span: drop the candidate, but keep the
            // review flag (an uncertain inner match makes the outer one uncertain as well).
            if (overlapping.Any(a => a.Start <= candidate.Start && candidate.End <= a.End))
            {
                continue;
            }

            if (candidate.Source == DetectionSource.Manual)
            {
                // Manual selection overrides automatic detections it touches.
                foreach (var o in overlapping)
                {
                    accepted.Remove(o);
                }

                accepted.Add(candidate);
                continue;
            }

            // Partial overlap: extend the strongest overlapping span to the union.
            var strongest = overlapping[0];
            var start = Math.Min(strongest.Start, candidate.Start);
            var end = Math.Max(strongest.End, candidate.End);
            foreach (var o in overlapping)
            {
                start = Math.Min(start, o.Start);
                end = Math.Max(end, o.End);
                accepted.Remove(o);
            }

            var union = new Detection(start, text[start..end], strongest.Category, strongest.Source)
            {
                NeedsReview = true,
                Note = "Merged overlapping detections; please verify the span",
            };
            accepted.Add(union);
        }

        accepted.Sort((a, b) => a.Start.CompareTo(b.Start));
        return accepted;
    }
}
