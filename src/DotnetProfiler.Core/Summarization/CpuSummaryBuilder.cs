using DotnetProfiler.Core.Parsing;

namespace DotnetProfiler.Core.Summarization;

public sealed class CpuSummaryBuilder
{
    private const int MaxPathDepth = 40;

    /// <summary>EventPipe sample profiler ticks ~every 1 ms per thread.</summary>
    public const double SampleIntervalMs = 1.0;

    public CpuSummary Build(
        CallTree tree, int topN, string sessionId, string traceFile, TargetInfo target,
        IEnumerable<string>? extraWarnings = null)
    {
        long total = Math.Max(1, tree.TotalSamples); // avoid div-by-zero on empty traces

        var hotMethods = AggregateMethods(tree.Root)
            .OrderByDescending(m => m.ExclusiveSamples)
            .ThenByDescending(m => m.InclusiveSamples)
            .Take(topN)
            .Select(m => m with
            {
                ExclusivePercent = Percent(m.ExclusiveSamples, total),
                InclusivePercent = Percent(m.InclusiveSamples, total),
            })
            .ToList();

        var hotPaths = CollectLeafPaths(tree.Root)
            .OrderByDescending(p => p.Samples)
            .Take(topN)
            .Select(p => new HotPath(p.Frames, p.Samples, Percent(p.Samples, total)))
            .ToList();

        return new CpuSummary
        {
            SessionId = sessionId,
            TraceFile = traceFile,
            Target = target,
            DurationSeconds = Math.Round(tree.TraceDuration.TotalSeconds, 2),
            TotalSamples = tree.TotalSamples,
            SampleIntervalMs = SampleIntervalMs,
            HotMethods = hotMethods,
            HotPaths = hotPaths,
            Warnings = [.. tree.Warnings, .. extraWarnings ?? []],
        };
    }

    /// <summary>
    /// Per-method rollup. Exclusive sums plainly; inclusive counts a sample once per
    /// method even under recursion (only the topmost occurrence on each path counts).
    /// </summary>
    private static List<HotMethod> AggregateMethods(CallTreeNode root)
    {
        var agg = new Dictionary<(string Module, string Method), (long Excl, long Incl)>();
        var active = new HashSet<(string, string)>();

        void Walk(CallTreeNode node)
        {
            var key = (node.Module, node.MethodName);
            bool topmost = !active.Contains(key);
            var cur = agg.GetValueOrDefault(key);
            agg[key] = (cur.Excl + node.ExclusiveSamples, cur.Incl + (topmost ? node.InclusiveSamples : 0));

            if (topmost) active.Add(key);
            foreach (var child in node.Children) Walk(child);
            if (topmost) active.Remove(key);
        }

        foreach (var threadNode in root.Children) // skip ROOT and thread pseudo-nodes in the rollup
            foreach (var child in threadNode.Children)
                Walk(child);

        return agg.Select(kv => new HotMethod(
            kv.Key.Method, kv.Key.Module, kv.Value.Excl, 0, kv.Value.Incl, 0)).ToList();
    }

    private static List<(IReadOnlyList<string> Frames, long Samples)> CollectLeafPaths(CallTreeNode root)
    {
        var results = new List<(IReadOnlyList<string>, long)>();
        var stack = new List<string>();

        void Walk(CallTreeNode node)
        {
            stack.Add(FrameLabel(node));
            if (node.ExclusiveSamples > 0)
                results.Add((Render(stack), node.ExclusiveSamples));
            foreach (var child in node.Children) Walk(child);
            stack.RemoveAt(stack.Count - 1);
        }

        foreach (var threadNode in root.Children)
            Walk(threadNode);
        return results;
    }

    private static string FrameLabel(CallTreeNode node) =>
        node.Module.Length == 0 ? node.MethodName : $"{node.Module}!{node.MethodName}";

    private static IReadOnlyList<string> Render(List<string> frames)
    {
        // Collapse consecutive identical frames (self-recursion) into one annotated frame.
        var collapsed = new List<string>(frames.Count);
        for (int i = 0; i < frames.Count;)
        {
            int j = i;
            while (j < frames.Count && frames[j] == frames[i]) j++;
            collapsed.Add(j - i > 1 ? $"{frames[i]} [recursion x{j - i}]" : frames[i]);
            i = j;
        }

        if (collapsed.Count <= MaxPathDepth)
            return collapsed;

        // Keep the root and leaf ends; elide the middle.
        int keep = MaxPathDepth / 2;
        return
        [
            .. collapsed.Take(keep),
            $"... {collapsed.Count - 2 * keep} frames elided ...",
            .. collapsed.Skip(collapsed.Count - keep),
        ];
    }

    private static double Percent(long samples, long total) =>
        Math.Round(100.0 * samples / total, 1);
}
