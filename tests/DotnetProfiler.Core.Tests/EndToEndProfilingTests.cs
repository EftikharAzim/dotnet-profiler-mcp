using DotnetProfiler.Core;
using DotnetProfiler.Core.Collection;
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
