using DotnetProfiler.Core;
using DotnetProfiler.Core.Collection;
using DotnetProfiler.Core.Parsing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DotnetProfiler.Core.Tests;

[Trait("Category", "Integration")]
public class EndToEndProfilingTests
{
    [Fact]
    public async Task AttachMode_ProducesReadableTrace()
    {
        var process = TestEnvironment.StartBurnerApp(burnSeconds: 30);
        var tracePath = Path.Combine(Path.GetTempPath(), $"dpmcp-test-{Guid.NewGuid():N}.nettrace");
        try
        {
            var adapter = new EventPipeCpuAdapter();
            var result = await adapter.CollectAsync(
                ProfilingTarget.ForPid(process.Id),
                TimeSpan.FromSeconds(5),
                tracePath,
                CancellationToken.None);

            Assert.Equal(tracePath, result.TraceFilePath);
            Assert.False(result.TargetExitedEarly);
            Assert.True(new FileInfo(tracePath).Length > 0, "trace file is empty");

            // The trace must convert to ETLX and open — this is what the parsing layer relies on.
            string etlx = TraceLog.CreateFromEventPipeDataFile(tracePath);
            try
            {
                using var traceLog = new TraceLog(etlx);
                Assert.True(traceLog.Events.Any(), "trace contains no events");
            }
            finally
            {
                File.Delete(etlx);
            }
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            File.Delete(tracePath);
        }
    }

    [Fact]
    public async Task AttachMode_Analyze_BurnCpuDominates()
    {
        var process = TestEnvironment.StartBurnerApp(burnSeconds: 30);
        var tracePath = Path.Combine(Path.GetTempPath(), $"dpmcp-test-{Guid.NewGuid():N}.nettrace");
        try
        {
            var adapter = new EventPipeCpuAdapter();
            await adapter.CollectAsync(
                ProfilingTarget.ForPid(process.Id), TimeSpan.FromSeconds(5), tracePath, CancellationToken.None);

            var tree = new TraceAnalyzer().Analyze(tracePath);

            Assert.True(tree.TotalSamples > 500, $"expected >500 samples, got {tree.TotalSamples}");
            Assert.DoesNotContain(tree.Warnings, w => w.Contains("unresolved"));

            // BurnCpu must be where the samples land. Assert on inclusive: exclusive
            // attribution shifts with inlining/build config, inclusive does not.
            long burnInclusive = MaxInclusive(tree.Root, m => m.Contains("BurnCpu"));
            double burnPercent = 100.0 * burnInclusive / tree.TotalSamples;
            Assert.True(burnPercent > 50, $"BurnCpu has {burnPercent:F1}% inclusive, expected > 50%");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            File.Delete(tracePath);
            File.Delete(Path.ChangeExtension(tracePath, ".etlx"));
        }
    }

    private static long MaxInclusive(CallTreeNode node, Func<string, bool> methodMatch)
    {
        long max = methodMatch(node.MethodName) ? node.InclusiveSamples : 0;
        foreach (var child in node.Children)
            max = Math.Max(max, MaxInclusive(child, methodMatch));
        return max;
    }

    [Fact]
    public async Task StartStop_Analyze_BurnCpuDominates()
    {
        var process = TestEnvironment.StartBurnerApp(burnSeconds: 30);
        var tracePath = Path.Combine(Path.GetTempPath(), $"dpmcp-test-{Guid.NewGuid():N}.nettrace");
        try
        {
            var adapter = new EventPipeCpuAdapter();
            var live = await adapter.StartAsync(process.Id, tracePath, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(5));
            var result = await adapter.StopAsync(live, CancellationToken.None);

            Assert.False(result.TargetExitedEarly);
            Assert.True(result.ActualDuration >= TimeSpan.FromSeconds(4),
                $"expected >=4s of collection, got {result.ActualDuration.TotalSeconds:F1}s");

            var tree = new TraceAnalyzer().Analyze(result.TraceFilePath);
            Assert.True(tree.TotalSamples > 500, $"expected >500 samples, got {tree.TotalSamples}");
            Assert.DoesNotContain(tree.Warnings, w => w.Contains("unresolved"));

            long burnInclusive = MaxInclusive(tree.Root, m => m.Contains("BurnCpu"));
            double burnPercent = 100.0 * burnInclusive / tree.TotalSamples;
            Assert.True(burnPercent > 50, $"BurnCpu has {burnPercent:F1}% inclusive, expected > 50%");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            File.Delete(tracePath);
            File.Delete(Path.ChangeExtension(tracePath, ".etlx"));
        }
    }

    [Fact]
    public async Task LaunchMode_ProfilesFromStartup_BurnCpuDominates()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"dpmcp-test-{Guid.NewGuid():N}.nettrace");
        try
        {
            var adapter = new EventPipeCpuAdapter();
            var result = await adapter.CollectAsync(
                ProfilingTarget.ForLaunch($"\"{TestEnvironment.DotnetPath}\" \"{TestEnvironment.BurnerAppDll}\" 30"),
                TimeSpan.FromSeconds(5),
                tracePath,
                CancellationToken.None);

            Assert.False(result.TargetExitedEarly);
            var tree = new TraceAnalyzer().Analyze(tracePath);
            Assert.True(tree.TotalSamples > 500, $"expected >500 samples, got {tree.TotalSamples}");

            long burnInclusive = MaxInclusive(tree.Root, m => m.Contains("BurnCpu"));
            double burnPercent = 100.0 * burnInclusive / tree.TotalSamples;
            Assert.True(burnPercent > 50, $"BurnCpu has {burnPercent:F1}% inclusive, expected > 50%");
        }
        finally
        {
            File.Delete(tracePath);
            File.Delete(Path.ChangeExtension(tracePath, ".etlx"));
        }
    }

    [Fact]
    public async Task LaunchMode_ChildExitsImmediately_ThrowsLaunchConnectTimeout()
    {
        var adapter = new EventPipeCpuAdapter();
        var ex = await Assert.ThrowsAsync<ProfilingException>(() =>
            adapter.CollectAsync(
                ProfilingTarget.ForLaunch("/usr/bin/true"), TimeSpan.FromSeconds(1),
                Path.Combine(Path.GetTempPath(), "never-written.nettrace"), CancellationToken.None));
        Assert.Equal(ProfilingException.Codes.LaunchConnectTimeout, ex.Code);
    }

    [Fact]
    public async Task AttachMode_NonDotnetPid_ThrowsNotADotnetProcess()
    {
        // PID 1 (launchd on macOS) is alive and definitely not .NET.
        var adapter = new EventPipeCpuAdapter();
        var ex = await Assert.ThrowsAsync<ProfilingException>(() =>
            adapter.CollectAsync(
                ProfilingTarget.ForPid(1), TimeSpan.FromSeconds(1),
                Path.Combine(Path.GetTempPath(), "never-written.nettrace"), CancellationToken.None));
        Assert.Equal(ProfilingException.Codes.NotADotnetProcess, ex.Code);
    }

    [Fact]
    public async Task AttachMode_MissingPid_ThrowsProcessNotFound()
    {
        var adapter = new EventPipeCpuAdapter();
        var ex = await Assert.ThrowsAsync<ProfilingException>(() =>
            adapter.CollectAsync(
                ProfilingTarget.ForPid(int.MaxValue - 7), TimeSpan.FromSeconds(1),
                Path.Combine(Path.GetTempPath(), "never-written.nettrace"), CancellationToken.None));
        Assert.Equal(ProfilingException.Codes.ProcessNotFound, ex.Code);
    }
}
