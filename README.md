# dotnet-profiler-mcp

An MCP server that profiles .NET applications and returns LLM-friendly summaries.
Built on **EventPipe** (`Microsoft.Diagnostics.NETCore.Client` + `TraceEvent`) — the
officially supported, cross-platform, admin-free diagnostics stack — not the
Visual Studio Diagnostics Hub.

Runs on macOS, Linux, and Windows. Developed and continuously tested on macOS
(arm64); on Windows, launch mode uses a named pipe instead of a unix socket for
the diagnostic port — same code path otherwise, but less battle-tested there.

**Current scope:** CPU sampling with hot-method / hot-path summaries — one-shot
and interactive start/stop. Planned next: runtime counters, memory (gcdump +
allocation hotspots), thread stacks, async (TPL activity graph) — see [plan.md](plan.md)
for the full roadmap and progress.

## Tools

| Tool | What it does |
|---|---|
| `profile_cpu` | Attach to a running .NET process by `pid`, **or** launch a `launchCommand` and profile it from its very first instruction (diagnostic-port suspend mode). Duration-bound (1–120 s). Returns top-N hot methods (exclusive/inclusive %) and collapsed hot call paths as JSON, plus the raw `.nettrace` path. |
| `start_cpu_profile` | Start an **open-ended** CPU session on a running process by `pid` (attach mode only). Returns a `sessionId` immediately; sampling continues in the background. |
| `stop_cpu_profile` | Stop a session started by `start_cpu_profile` and return the same summary shape as `profile_cpu`. Stopping an unknown or already-stopped session id returns `session_not_found`. |
| `get_hot_paths` | Re-summarize an existing trace (session id or `.nettrace` path) with a different `topN` — no re-profiling. |

For an interactive demo of the start/stop pair, see the sibling `CpuHogDemo`
project: it connects to this server as an MCP client and maps console keys
`s`/`x` to `start_cpu_profile`/`stop_cpu_profile` against its own pid.

Errors come back as structured JSON (`{"error":{"code","message","hint"}}`) with stable
codes: `invalid_arguments`, `process_not_found`, `not_a_dotnet_process`,
`launch_connect_timeout`, `trace_unreadable`, `session_not_found`.

Artifacts (`.nettrace` + cached `.etlx`) live in `~/.dotnet-profiler-mcp/artifacts/`.
Session ids keep resolving after a server restart (file-layout fallback).

## Setup

```bash
dotnet build
```

Register with Claude Code (adjust paths; on macOS/Linux `dotnet` may not be on
PATH — use the absolute host path):

```bash
claude mcp add dotnet-profiler -- \
  ~/.dotnet/dotnet \
  ~/dev/projects/dotnet-profiler-mcp/src/DotnetProfiler.McpServer/bin/Debug/net9.0/DotnetProfiler.McpServer.dll
```

On Windows `dotnet` is normally on PATH:

```powershell
claude mcp add dotnet-profiler -- dotnet C:\path\to\dotnet-profiler-mcp\src\DotnetProfiler.McpServer\bin\Debug\net9.0\DotnetProfiler.McpServer.dll
```

Or in JSON config:

```json
{
  "mcpServers": {
    "dotnet-profiler": {
      "command": "/Users/<you>/.dotnet/dotnet",
      "args": ["/path/to/dotnet-profiler-mcp/src/DotnetProfiler.McpServer/bin/Debug/net9.0/DotnetProfiler.McpServer.dll"]
    }
  }
}
```

## Tests

```bash
dotnet test                                    # all (integration attaches to a real child process)
dotnet test --filter "Category!=Integration"   # unit only
```

The integration suite launches `samples/BurnerApp` (a busy-loop app) and asserts the
profiler attributes >50% of samples to `Burner.BurnCpu` in both attach and launch modes.

## Architecture

Three layers, MCP is just a frontend:

1. **Collection** — `ICollectorAdapter`; today `EventPipeCpuAdapter`
   (`Microsoft-DotNETCore-SampleProfiler` + `Microsoft-Windows-DotNETRuntime:0x100003801D`,
   the dotnet-common set). New mechanisms = new adapters.
2. **Parsing** — `TraceAnalyzer`: `.nettrace` → ETLX → `TraceLog` → `CallTree`
   (mechanism-agnostic, inclusive/exclusive sample counts, lost-event and
   missing-rundown warnings).
3. **Summarization** — `CpuSummaryBuilder`: recursion-safe method rollup + collapsed
   hot paths, sized for an LLM context window.

Design notes worth knowing:

- **Rundown is sacred.** The session is always `Stop()`ped before disposal and the event
  stream drained to EOF — that's what flushes the method-name rundown. A trace whose
  frames are >50% unresolved gets an explicit warning in the summary.
- **Launch mode never leaks children.** A launched runtime starts suspended; if anything
  fails before `ResumeRuntime()`, the child is killed in `finally` (a suspended runtime
  would otherwise hang forever).
- **Sampling reality.** The EventPipe sample profiler ticks ~1 ms per thread whether the
  thread is on-CPU or blocked; percentages are of total samples, not wall-clock CPU.
  Idle runtime threads (threadpool waits, GC, timer) therefore often out-rank the
  genuinely busy method in raw percentages — look for your methods *appearing and
  resolving by name*, and compare exclusive samples between app methods, rather than
  expecting them to top the global list.
- **Live sessions are one-shot handles.** `stop_cpu_profile` atomically removes the
  session from the registry (`TakeLive`), so double-stop is structurally impossible —
  the second call gets `session_not_found`. There is currently no auto-stop for
  forgotten live sessions; see plan.md (Phase 1.5 gaps).
