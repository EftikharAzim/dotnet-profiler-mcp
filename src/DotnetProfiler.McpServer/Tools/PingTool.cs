using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DotnetProfiler.McpServer.Tools;

/// <summary>Placeholder tool proving tool discovery works; removed once profiling tools land.</summary>
[McpServerToolType]
public static class PingTool
{
    [McpServerTool(Name = "ping"), Description("Health check. Echoes the message back.")]
    public static string Ping([Description("Message to echo.")] string message) => $"pong: {message}";
}
