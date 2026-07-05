namespace DotnetProfiler.Core.Collection;

/// <summary>The process to profile: an existing PID or a command line to launch.</summary>
public sealed record ProfilingTarget
{
    public int? Pid { get; init; }
    public string? LaunchCommand { get; init; }
    public string? WorkingDirectory { get; init; }

    public bool IsLaunch => LaunchCommand is not null;

    public static ProfilingTarget ForPid(int pid) => new() { Pid = pid };

    public static ProfilingTarget ForLaunch(string command, string? workingDirectory = null) =>
        new() { LaunchCommand = command, WorkingDirectory = workingDirectory };
}
