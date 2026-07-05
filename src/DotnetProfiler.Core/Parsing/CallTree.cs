namespace DotnetProfiler.Core.Parsing;

/// <summary>Aggregated CPU sample tree for one trace. Mechanism-agnostic: the summarizer never sees TraceEvent types.</summary>
public sealed class CallTree
{
    public required long TotalSamples { get; init; }
    public required TimeSpan TraceDuration { get; init; }
    public required CallTreeNode Root { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class CallTreeNode
{
    public required string MethodName { get; init; }
    public required string Module { get; init; }

    public long InclusiveSamples { get; internal set; }
    public long ExclusiveSamples { get; internal set; }

    internal Dictionary<(string Module, string Method), CallTreeNode> ChildMap { get; } = [];

    public IReadOnlyCollection<CallTreeNode> Children => ChildMap.Values;

    internal CallTreeNode GetOrAddChild(string module, string method)
    {
        var key = (module, method);
        if (!ChildMap.TryGetValue(key, out var child))
        {
            child = new CallTreeNode { MethodName = method, Module = module };
            ChildMap[key] = child;
        }
        return child;
    }
}
