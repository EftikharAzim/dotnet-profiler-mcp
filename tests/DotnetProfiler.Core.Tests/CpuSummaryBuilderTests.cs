using DotnetProfiler.Core.Parsing;
using DotnetProfiler.Core.Summarization;

namespace DotnetProfiler.Core.Tests;

public class CpuSummaryBuilderTests
{
    private static readonly TargetInfo Target = new("attach", 123, null);

    private static CpuSummary Build(CallTree tree, int topN = 10) =>
        new CpuSummaryBuilder().Build(tree, topN, "cpu-test-0000", "/tmp/t.nettrace", Target);

    /// <summary>ROOT -> Thread(1) -> Main -> {Hot (90 excl), Cold (10 excl)}.</summary>
    private static CallTree SimpleTree()
    {
        var root = new CallTreeNode { MethodName = "ROOT", Module = "" };
        var thread = root.GetOrAddChild("", "Thread (1)");
        var main = thread.GetOrAddChild("App", "Program.Main()");
        var hot = main.GetOrAddChild("App", "Hot()");
        var cold = main.GetOrAddChild("App", "Cold()");

        hot.ExclusiveSamples = 90; hot.InclusiveSamples = 90;
        cold.ExclusiveSamples = 10; cold.InclusiveSamples = 10;
        main.InclusiveSamples = 100;
        thread.InclusiveSamples = 100;
        root.InclusiveSamples = 100;

        return new CallTree { TotalSamples = 100, TraceDuration = TimeSpan.FromSeconds(1), Root = root, Warnings = [] };
    }

    [Fact]
    public void HotMethods_RankedByExclusive_WithPercentages()
    {
        var summary = Build(SimpleTree());

        Assert.Equal("Hot()", summary.HotMethods[0].Method);
        Assert.Equal(90, summary.HotMethods[0].ExclusiveSamples);
        Assert.Equal(90.0, summary.HotMethods[0].ExclusivePercent);
        Assert.Equal(90.0, summary.HotMethods[0].InclusivePercent);

        var main = summary.HotMethods.Single(m => m.Method == "Program.Main()");
        Assert.Equal(0, main.ExclusiveSamples);
        Assert.Equal(100.0, main.InclusivePercent);
    }

    [Fact]
    public void HotPaths_RootToLeaf_TopRanked()
    {
        var summary = Build(SimpleTree());

        var top = summary.HotPaths[0];
        Assert.Equal(90, top.Samples);
        Assert.Equal(90.0, top.Percent);
        Assert.Equal(["Thread (1)", "App!Program.Main()", "App!Hot()"], top.Frames);
    }

    [Fact]
    public void Recursion_InclusiveCountedOncePerSample()
    {
        // Thread(1) -> Rec -> Rec -> Rec (50 excl): naive inclusive sum would be 150.
        var root = new CallTreeNode { MethodName = "ROOT", Module = "" };
        var thread = root.GetOrAddChild("", "Thread (1)");
        var r1 = thread.GetOrAddChild("App", "Rec()");
        var r2 = r1.GetOrAddChild("App", "Rec()");
        var r3 = r2.GetOrAddChild("App", "Rec()");
        r3.ExclusiveSamples = 50;
        r1.InclusiveSamples = r2.InclusiveSamples = r3.InclusiveSamples = 50;
        thread.InclusiveSamples = 50;
        root.InclusiveSamples = 50;
        var tree = new CallTree { TotalSamples = 50, TraceDuration = TimeSpan.FromSeconds(1), Root = root, Warnings = [] };

        var summary = Build(tree);

        var rec = summary.HotMethods.Single(m => m.Method == "Rec()");
        Assert.Equal(50, rec.InclusiveSamples);
        Assert.Equal(100.0, rec.InclusivePercent);

        // And the path collapses the self-recursion run.
        Assert.Contains("App!Rec() [recursion x3]", summary.HotPaths[0].Frames);
    }

    [Fact]
    public void EmptyTree_NoCrash_NoPaths()
    {
        var root = new CallTreeNode { MethodName = "ROOT", Module = "" };
        var tree = new CallTree
        {
            TotalSamples = 0,
            TraceDuration = TimeSpan.Zero,
            Root = root,
            Warnings = ["No CPU samples found in trace."],
        };

        var summary = Build(tree);

        Assert.Empty(summary.HotMethods);
        Assert.Empty(summary.HotPaths);
        Assert.Single(summary.Warnings);
    }
}
