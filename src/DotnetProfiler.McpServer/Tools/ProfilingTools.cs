using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetProfiler.Core;
using DotnetProfiler.Core.Collection;
using DotnetProfiler.Core.Parsing;
using DotnetProfiler.Core.Sessions;
using DotnetProfiler.Core.Summarization;
using ModelContextProtocol.Server;

namespace DotnetProfiler.McpServer.Tools;

[McpServerToolType]
public sealed class ProfilingTools(SessionRegistry registry, ICollectorAdapter collector)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "profile_cpu"), Description(
        "Profile CPU usage of a .NET application via EventPipe sampling (no admin rights needed). " +
        "Attach to a running process by pid, or launch a command and profile it from startup. " +
        "Returns hot methods and hot call paths as JSON; the raw .nettrace path is included for follow-up analysis.")]
    public async Task<string> ProfileCpu(
        [Description("PID of a running .NET process to attach to. Mutually exclusive with launchCommand.")]
        int? pid = null,
        [Description("Command line to launch and profile from startup, e.g. '/path/to/dotnet app.dll arg'. Mutually exclusive with pid.")]
        string? launchCommand = null,
        [Description("Working directory for launchCommand (defaults to the server's cwd).")]
        string? workingDirectory = null,
        [Description("Sampling duration in seconds. Clamped to 1-120. Default 10.")]
        int durationSeconds = 10,
        [Description("How many hot methods / hot paths to return. Default 10.")]
        int topN = 10,
        CancellationToken ct = default)
    {
        try
        {
            if (pid is null == (launchCommand is null))
            {
                throw new ProfilingException(
                    ProfilingException.Codes.InvalidArguments,
                    "Provide exactly one of 'pid' or 'launchCommand'.");
            }

            var extraWarnings = new List<string>();
            int clamped = Math.Clamp(durationSeconds, 1, 120);
            if (clamped != durationSeconds)
                extraWarnings.Add($"durationSeconds clamped from {durationSeconds} to {clamped}.");
            topN = Math.Clamp(topN, 1, 100);

            var target = pid is int p
                ? ProfilingTarget.ForPid(p)
                : ProfilingTarget.ForLaunch(launchCommand!, workingDirectory);

            var session = registry.Create("cpu");
            var result = await collector.CollectAsync(
                target, TimeSpan.FromSeconds(clamped), session.TraceFilePath, ct);
            extraWarnings.AddRange(result.Warnings);

            var tree = new TraceAnalyzer().Analyze(result.TraceFilePath);
            var summary = new CpuSummaryBuilder().Build(
                tree, topN, session.Id, result.TraceFilePath,
                new TargetInfo(pid is null ? "launch" : "attach", result.TargetPid, launchCommand),
                extraWarnings);

            return JsonSerializer.Serialize(summary, JsonOptions);
        }
        catch (Exception ex)
        {
            return ErrorJson(ex);
        }
    }

    [McpServerTool(Name = "get_hot_paths"), Description(
        "Re-summarize an existing CPU trace: pass a session id from profile_cpu or an absolute path " +
        "to a .nettrace file. Useful to get more hot paths (higher topN) without re-profiling.")]
    public Task<string> GetHotPaths(
        [Description("Session id from profile_cpu, or absolute path to a .nettrace file.")]
        string session,
        [Description("How many hot methods / hot paths to return. Default 10.")]
        int topN = 10,
        CancellationToken ct = default)
    {
        try
        {
            topN = Math.Clamp(topN, 1, 100);
            string tracePath = registry.ResolveTracePath(session);

            var tree = new TraceAnalyzer().Analyze(tracePath);
            var summary = new CpuSummaryBuilder().Build(
                tree, topN, session, tracePath, new TargetInfo("trace", null, null));

            return Task.FromResult(JsonSerializer.Serialize(summary, JsonOptions));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorJson(ex));
        }
    }

    private static string ErrorJson(Exception ex)
    {
        var (code, message, hint) = ex switch
        {
            ProfilingException pe => (pe.Code, pe.Message, pe.Hint),
            OperationCanceledException => ("cancelled", "The operation was cancelled.", (string?)null),
            _ => ("internal_error", ex.Message, ex.GetType().FullName),
        };
        return JsonSerializer.Serialize(new { error = new { code, message, hint } }, JsonOptions);
    }
}
