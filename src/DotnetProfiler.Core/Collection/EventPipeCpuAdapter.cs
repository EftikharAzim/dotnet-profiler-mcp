using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetProfiler.Core.Collection;

/// <summary>
/// CPU sampling over EventPipe: the runtime sample profiler (~1 ms/thread) plus the
/// dotnet-common runtime keyword set so rundown can resolve managed method names.
/// </summary>
public sealed class EventPipeCpuAdapter : ICollectorAdapter
{
    /// <summary>dotnet-common: GC, AssemblyLoader, Loader, JIT, Exceptions, Threading, ILToNativeMap, Compilation.</summary>
    private const long DotnetCommonKeywords = 0x1_0000_3801D;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public string Name => "eventpipe-cpu";

    public async Task<CollectionResult> CollectAsync(
        ProfilingTarget target,
        TimeSpan duration,
        string outputTracePath,
        CancellationToken ct)
    {
        if (target.IsLaunch)
        {
            return await CollectFromLaunchAsync(target, duration, outputTracePath, ct);
        }

        int pid = target.Pid ?? throw new ProfilingException(
            ProfilingException.Codes.InvalidArguments, "ProfilingTarget has neither Pid nor LaunchCommand.");

        EnsureAttachable(pid);
        var client = new DiagnosticsClient(pid);
        return await RunSessionAsync(client, pid, duration, outputTracePath, ct);
    }

    private static Task<CollectionResult> CollectFromLaunchAsync(
        ProfilingTarget target, TimeSpan duration, string outputTracePath, CancellationToken ct)
    {
        // Implemented in the launch-mode milestone (diagnostic-port listen + suspend).
        throw new NotSupportedException("Launch mode is not implemented yet; attach by PID.");
    }

    /// <summary>Distinguish "no such process" from "alive but not .NET" before attaching.</summary>
    private static void EnsureAttachable(int pid)
    {
        bool processAlive;
        try
        {
            using var p = Process.GetProcessById(pid);
            processAlive = !p.HasExited;
        }
        catch (ArgumentException)
        {
            processAlive = false;
        }

        if (!processAlive)
        {
            throw new ProfilingException(
                ProfilingException.Codes.ProcessNotFound,
                $"No running process with PID {pid}.",
                hint: DescribeDiscoverableProcesses());
        }

        if (!DiagnosticsClient.GetPublishedProcesses().Contains(pid))
        {
            throw new ProfilingException(
                ProfilingException.Codes.NotADotnetProcess,
                $"Process {pid} is running but exposes no .NET diagnostic endpoint.",
                hint: DescribeDiscoverableProcesses());
        }
    }

    internal static string DescribeDiscoverableProcesses()
    {
        var pids = DiagnosticsClient.GetPublishedProcesses().Take(20).Select(pid =>
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return $"{pid} ({p.ProcessName})";
            }
            catch
            {
                return pid.ToString();
            }
        });
        var list = string.Join(", ", pids);
        return list.Length == 0
            ? "No .NET processes with diagnostic endpoints found."
            : $"Discoverable .NET processes: {list}";
    }

    internal static async Task<CollectionResult> RunSessionAsync(
        DiagnosticsClient client, int pid, TimeSpan duration, string outputTracePath, CancellationToken ct)
    {
        var providers = new[]
        {
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, DotnetCommonKeywords),
        };

        var warnings = new List<string>();
        bool targetExitedEarly = false;
        var stopwatch = Stopwatch.StartNew();

        using var session = await client.StartEventPipeSessionAsync(
            providers, requestRundown: true, circularBufferMB: 256, token: ct);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputTracePath))!);
        var fileStream = new FileStream(
            outputTracePath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16, useAsync: true);

        await using (fileStream.ConfigureAwait(false))
        {
            // Drain immediately or the runtime-side circular buffer overflows and drops events.
            var copyTask = CopyStreamAsync(session.EventStream, fileStream);

            try
            {
                await Task.Delay(duration, ct);
            }
            catch (OperationCanceledException)
            {
                warnings.Add("Collection cancelled before the requested duration elapsed.");
            }

            // If the copy finished before the duration did, the target went away mid-session.
            if (copyTask.IsCompleted)
            {
                targetExitedEarly = true;
                warnings.Add("Target process exited before the requested duration elapsed; summary reflects a partial trace.");
            }
            else
            {
                // Stop() flushes rundown (method-name resolution) through the same stream.
                // Skipping it, or disposing first, yields a trace full of unresolved frames.
                using var stopCts = new CancellationTokenSource(StopTimeout);
                try
                {
                    await session.StopAsync(stopCts.Token);
                }
                catch (OperationCanceledException)
                {
                    warnings.Add($"Session stop timed out after {StopTimeout.TotalSeconds:F0}s; rundown may be missing and method names may not resolve.");
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or DiagnosticsClientException)
                {
                    targetExitedEarly = true;
                    warnings.Add($"Target process ended during session stop ({ex.GetType().Name}); summary reflects a partial trace.");
                }
            }

            try
            {
                await copyTask;
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
            {
                targetExitedEarly = true;
                warnings.Add($"Event stream ended early ({ex.GetType().Name}); summary reflects a partial trace.");
            }
        }

        stopwatch.Stop();

        if (new FileInfo(outputTracePath).Length == 0)
        {
            throw new ProfilingException(
                ProfilingException.Codes.TraceUnreadable,
                "Collection produced an empty trace file.",
                hint: "The target may have exited immediately or rejected the session.");
        }

        return new CollectionResult(
            TraceFilePath: outputTracePath,
            RequestedDuration: duration,
            ActualDuration: stopwatch.Elapsed,
            TargetPid: pid,
            TargetExitedEarly: targetExitedEarly,
            Warnings: warnings);
    }

    private static async Task CopyStreamAsync(Stream source, Stream destination)
    {
        // Deliberately NOT cancellable: after StopAsync we must drain to EOF so rundown
        // events reach the file. Session disposal unblocks the read if the runtime hangs.
        await source.CopyToAsync(destination);
        await destination.FlushAsync();
    }
}
