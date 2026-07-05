# dotnet-profiler-mcp

An MCP server that profiles .NET applications and returns LLM-friendly summaries.
Built on **EventPipe** (`Microsoft.Diagnostics.NETCore.Client` + `TraceEvent`) — the
officially supported, cross-platform, admin-free diagnostics stack — not the
Visual Studio Diagnostics Hub.

**Phase 0+1 scope (current):** CPU sampling with hot-method / hot-path summaries.
Planned next: memory (gcdump + allocation hotspots), then async (TPL activity graph).

## Tools

| Tool | What it does |
|---|---|
| `profile_cpu` | Attach to a running .NET process by `pid`, **or** launch a `launchCommand` and profile it from its very first instruction (diagnostic-port suspend mode). Returns top-N hot methods (exclusive/inclusive %) and collapsed hot call paths as JSON, plus the raw `.nettrace` path. |
| `get_hot_paths` | Re-summarize an existing trace (session id or `.nettrace` path) with a different `topN` — no re-profiling. |

Errors come back as structured JSON (`{"error":{"code","message","hint"}}`) with stable
codes: `invalid_arguments`, `process_not_found`, `not_a_dotnet_process`,
`launch_connect_timeout`, `trace_unreadable`, `session_not_found`.

Artifacts (`.nettrace` + cached `.etlx`) live in `~/.dotnet-profiler-mcp/artifacts/`.
Session ids keep resolving after a server restart (file-layout fallback).

## Setup

```bash
dotnet build
```

Register with Claude Code (adjust paths; `dotnet` may not be on PATH — use the absolute host path):

```bash
claude mcp add dotnet-profiler -- \
  ~/.dotnet/dotnet \
  ~/dev/projects/dotnet-profiler-mcp/src/DotnetProfiler.McpServer/bin/Debug/net9.0/DotnetProfiler.McpServer.dll
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
