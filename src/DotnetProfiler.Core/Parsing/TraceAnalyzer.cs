using DotnetProfiler.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DotnetProfiler.Core.Parsing;

/// <summary>
/// Turns a .nettrace into a <see cref="CallTree"/> of CPU samples.
/// Conversion goes through ETLX (the only path that yields call stacks); the .etlx is
/// kept beside the .nettrace so re-summarizing is cheap.
/// </summary>
public sealed class TraceAnalyzer
{
    private const string SampleProviderName = "Microsoft-DotNETCore-SampleProfiler";
    private const string SampleEventName = "Thread/Sample";

    public CallTree Analyze(string nettracePath)
    {
        if (!File.Exists(nettracePath))
        {
            throw new ProfilingException(
                ProfilingException.Codes.TraceUnreadable, $"Trace file not found: {nettracePath}");
        }

        string etlxPath = Path.ChangeExtension(nettracePath, ".etlx");
        try
        {
            if (!File.Exists(etlxPath) || File.GetLastWriteTimeUtc(etlxPath) < File.GetLastWriteTimeUtc(nettracePath))
            {
                var options = new TraceLogOptions { ConversionLog = TextWriter.Null };
                TraceLog.CreateFromEventPipeDataFile(nettracePath, etlxPath, options);
            }

            using var traceLog = new TraceLog(etlxPath);
            return BuildTree(traceLog);
        }
        catch (ProfilingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProfilingException(
                ProfilingException.Codes.TraceUnreadable,
                $"Failed to parse trace: {ex.Message}",
                hint: "The trace may be truncated (target crashed mid-collection) or not a .nettrace file.");
        }
    }

    private static CallTree BuildTree(TraceLog traceLog)
    {
        var root = new CallTreeNode { MethodName = "ROOT", Module = "" };
        long totalSamples = 0, totalFrames = 0, unresolvedFrames = 0;
        var framesBuffer = new List<(string Module, string Method)>(64);

        foreach (var evt in traceLog.Events)
        {
            if (evt.ProviderName != SampleProviderName || evt.EventName != SampleEventName)
                continue;

            var callStack = evt.CallStack();
            if (callStack is null)
                continue;

            totalSamples++;

            // Walk leaf -> root, then insert root -> leaf under a per-thread node.
            framesBuffer.Clear();
            for (var frame = callStack; frame is not null; frame = frame.Caller)
            {
                string method = frame.CodeAddress.FullMethodName;
                string module = frame.CodeAddress.ModuleFile?.Name ?? "?";
                totalFrames++;
                if (string.IsNullOrEmpty(method))
                {
                    unresolvedFrames++;
                    method = $"0x{frame.CodeAddress.Address:x}";
                }
                framesBuffer.Add((module, method));
            }

            var node = root.GetOrAddChild("", $"Thread ({evt.ThreadID})");
            node.InclusiveSamples++;
            for (int i = framesBuffer.Count - 1; i >= 0; i--)
            {
                node = node.GetOrAddChild(framesBuffer[i].Module, framesBuffer[i].Method);
                node.InclusiveSamples++;
            }
            node.ExclusiveSamples++;
        }

        root.InclusiveSamples = totalSamples;

        var warnings = new List<string>();
        if (traceLog.EventsLost > 0)
            warnings.Add($"Trace reports {traceLog.EventsLost} lost events; percentages may be skewed.");
        if (totalSamples == 0)
            warnings.Add("No CPU samples found in trace. Was the sample profiler provider enabled?");
        else if (unresolvedFrames > totalFrames / 2)
            warnings.Add(
                $"{100.0 * unresolvedFrames / totalFrames:F0}% of stack frames are unresolved — " +
                "rundown is likely missing (target killed before the session stopped cleanly?).");

        return new CallTree
        {
            TotalSamples = totalSamples,
            TraceDuration = traceLog.SessionDuration,
            Root = root,
            Warnings = warnings,
        };
    }
}
