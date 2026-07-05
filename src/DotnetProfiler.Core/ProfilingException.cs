namespace DotnetProfiler.Core;

/// <summary>
/// Error with a stable machine-readable code. The MCP layer serializes these into
/// {"error":{"code","message","hint"}} envelopes instead of letting exceptions cross the protocol.
/// </summary>
public sealed class ProfilingException(string code, string message, string? hint = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public string? Hint { get; } = hint;

    public static class Codes
    {
        public const string InvalidArguments = "invalid_arguments";
        public const string ProcessNotFound = "process_not_found";
        public const string NotADotnetProcess = "not_a_dotnet_process";
        public const string LaunchConnectTimeout = "launch_connect_timeout";
        public const string TraceUnreadable = "trace_unreadable";
        public const string SessionNotFound = "session_not_found";
    }
}
