using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetProfiler.Core.Collection;

/// <summary>An open-ended EventPipe CPU session started by StartAsync, pending StopAsync.</summary>
public sealed record LiveCpuSession(
    EventPipeSession Session,
    FileStream FileStream,
    Task CopyTask,
    int Pid,
    string OutputTracePath,
    DateTimeOffset StartedAt);
