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

    private static readonly TimeSpan LaunchConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Launch-suspend-resume: we listen on a diagnostic port, the child (started with
    /// DOTNET_DiagnosticPorts) suspends at startup and connects out to us, we start the
    /// EventPipe session and only then resume — so the profile covers startup too.
    /// </summary>
    private static async Task<CollectionResult> CollectFromLaunchAsync(
        ProfilingTarget target, TimeSpan duration, string outputTracePath, CancellationToken ct)
    {
        var (fileName, arguments) = CommandLine.Split(target.LaunchCommand!);

        // macOS caps unix socket paths around 104 chars — keep it short, in TMPDIR.
        string socketPath = Path.Combine(
            Path.GetTempPath(), $"dpmcp-{Guid.NewGuid().ToString("N")[..8]}.sock");

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = target.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["DOTNET_DiagnosticPorts"] = socketPath; // suspend is the default mode

        DiagnosticsClientConnector? connector = null;
        Process? child = null;
        var stderrTail = new Queue<string>();
        try
        {
            // Listen BEFORE launching so the socket exists when the runtime connects out.
            var connectorTask = DiagnosticsClientConnector.FromDiagnosticPort(socketPath, ct);

            try
            {
                child = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new ProfilingException(
                    ProfilingException.Codes.LaunchConnectTimeout,
                    $"Failed to start '{fileName}': {ex.Message}");
            }
            if (child is null)
            {
                throw new ProfilingException(
                    ProfilingException.Codes.LaunchConnectTimeout, $"Failed to start '{fileName}'.");
            }

            child.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderrTail)
                {
                    stderrTail.Enqueue(e.Data);
                    while (stderrTail.Count > 20) stderrTail.Dequeue();
                }
            };
            child.BeginErrorReadLine();
            child.BeginOutputReadLine(); // drain so the child can't block on a full pipe

            // Poll so a child that dies instantly fails fast instead of eating the full timeout.
            var deadline = DateTime.UtcNow + LaunchConnectTimeout;
            while (!connectorTask.IsCompleted && DateTime.UtcNow < deadline && !child.HasExited)
            {
                await Task.WhenAny(connectorTask, Task.Delay(250, ct));
            }

            if (!connectorTask.IsCompleted)
            {
                string tail;
                lock (stderrTail) tail = string.Join("\n", stderrTail);
                throw new ProfilingException(
                    ProfilingException.Codes.LaunchConnectTimeout,
                    child.HasExited
                        ? $"Child process exited (code {child.ExitCode}) before exposing a diagnostic connection."
                        : $"Child process did not connect within {LaunchConnectTimeout.TotalSeconds:F0}s. Is it a .NET (5+) app?",
                    hint: tail.Length > 0 ? $"Child stderr:\n{tail}" : null);
            }

            connector = await connectorTask;
            var client = connector.Instance;

            return await RunSessionAsync(
                client, child.Id, duration, outputTracePath, ct,
                onSessionStarted: () => client.ResumeRuntime());
        }
        finally
        {
            // The child was ours; never leave it running (a suspended runtime that was
            // never resumed would otherwise hang forever).
            if (child is not null && !child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            child?.Dispose();
            if (connector is not null)
                await connector.DisposeAsync();
            try { File.Delete(socketPath); } catch { /* best effort */ }
        }
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
        DiagnosticsClient client, int pid, TimeSpan duration, string outputTracePath, CancellationToken ct,
        Action? onSessionStarted = null)
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

        // In launch mode this resumes the suspended runtime — only after the session
        // is live, so startup is captured.
        onSessionStarted?.Invoke();

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
