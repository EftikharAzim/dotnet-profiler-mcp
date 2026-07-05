namespace DotnetProfiler.Core.Summarization;

/// <summary>Token-efficient CPU profile summary — the JSON the LLM sees. Raw trace stays on disk.</summary>
public sealed record CpuSummary
{
    public required string SessionId { get; init; }
    public required string TraceFile { get; init; }
    public required TargetInfo Target { get; init; }
    public required double DurationSeconds { get; init; }
    public required long TotalSamples { get; init; }
    public required double SampleIntervalMs { get; init; }
    public required IReadOnlyList<HotMethod> HotMethods { get; init; }
    public required IReadOnlyList<HotPath> HotPaths { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record TargetInfo(string Mode, int? Pid, string? Command);

public sealed record HotMethod(
    string Method,
    string Module,
    long ExclusiveSamples,
    double ExclusivePercent,
    long InclusiveSamples,
    double InclusivePercent);

public sealed record HotPath(
    IReadOnlyList<string> Frames, // root -> leaf, depth-capped, consecutive recursion collapsed
    long Samples,
    double Percent);
