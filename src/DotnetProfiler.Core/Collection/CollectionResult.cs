namespace DotnetProfiler.Core.Collection;

/// <summary>Outcome of a collection run. The raw trace stays on disk; downstream layers parse it.</summary>
public sealed record CollectionResult(
    string TraceFilePath,
    TimeSpan RequestedDuration,
    TimeSpan ActualDuration,
    int? TargetPid,
    bool TargetExitedEarly,
    IReadOnlyList<string> Warnings);
