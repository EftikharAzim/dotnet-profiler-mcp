using System.Collections.Concurrent;
using DotnetProfiler.Core;

namespace DotnetProfiler.Core.Sessions;

public sealed record ProfilingSession(string Id, string TraceFilePath, DateTimeOffset CreatedAt);

/// <summary>
/// Keeps traces on disk under an artifacts dir and maps session ids to them.
/// Ids survive server restarts because resolution falls back to the file layout.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, ProfilingSession> _sessions = new();

    public SessionRegistry(string? artifactsDirectory = null)
    {
        ArtifactsDirectory = artifactsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet-profiler-mcp", "artifacts");
        Directory.CreateDirectory(ArtifactsDirectory);
    }

    public string ArtifactsDirectory { get; }

    public ProfilingSession Create(string kind)
    {
        string id = $"{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var session = new ProfilingSession(id, Path.Combine(ArtifactsDirectory, $"{id}.nettrace"), DateTimeOffset.UtcNow);
        _sessions[id] = session;
        return session;
    }

    /// <summary>Accepts a session id (this run or a prior one) or a literal .nettrace path.</summary>
    public string ResolveTracePath(string sessionIdOrPath)
    {
        if (_sessions.TryGetValue(sessionIdOrPath, out var session))
            return session.TraceFilePath;

        if (sessionIdOrPath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
            && File.Exists(sessionIdOrPath))
            return Path.GetFullPath(sessionIdOrPath);

        string byId = Path.Combine(ArtifactsDirectory, $"{sessionIdOrPath}.nettrace");
        if (File.Exists(byId))
            return byId;

        throw new ProfilingException(
            ProfilingException.Codes.SessionNotFound,
            $"No session or trace file found for '{sessionIdOrPath}'.",
            hint: $"Pass a session id from profile_cpu or an absolute path to a .nettrace file. Artifacts dir: {ArtifactsDirectory}");
    }
}
