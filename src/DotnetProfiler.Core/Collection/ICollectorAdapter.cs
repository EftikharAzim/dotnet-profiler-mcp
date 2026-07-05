namespace DotnetProfiler.Core.Collection;

/// <summary>
/// One collection mechanism (EventPipe CPU sampling today; gcdump, allocation tracing,
/// async causality later). New mechanisms are new implementations of this interface.
/// </summary>
public interface ICollectorAdapter
{
    string Name { get; }

    Task<CollectionResult> CollectAsync(
        ProfilingTarget target,
        TimeSpan duration,
        string outputTracePath,
        CancellationToken ct);
}
