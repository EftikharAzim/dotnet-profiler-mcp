# dotnet-profiler-mcp — Roadmap

Goal: expose the full .NET diagnostics toolbox (`dotnet-trace`, `dotnet-counters`,
`dotnet-gcdump`, `dotnet-stack`, `dotnet-dump`) as MCP tools that return
LLM-friendly summaries, all on the same EventPipe / DiagnosticsClient stack —
cross-platform, admin-free, no Visual Studio dependency.

Legend: ✅ done · 🚧 in progress · ⬜ not started

---

## Phase 0+1 — CPU sampling ✅ (shipped)

| Tool | Status | Notes |
|---|---|---|
| `profile_cpu` | ✅ | One-shot, duration-bound (1–120 s). Attach by pid or launch-suspend-resume from first instruction. |
| `get_hot_paths` | ✅ | Re-summarize an existing trace (session id or `.nettrace` path) with a different `topN`. |

Foundation shipped with it: `ICollectorAdapter` abstraction, `SessionRegistry`
(ids survive restarts via file-layout fallback), `TraceAnalyzer`
(`.nettrace` → ETLX → recursion-safe `CallTree`), `CpuSummaryBuilder`
(hot methods + collapsed hot paths sized for a context window), structured error
envelope with stable codes, unit + integration test suite (17 tests).

## Phase 1.5 — Interactive start/stop CPU sessions ✅ (shipped 2026-07-05)

| Tool | Status | Notes |
|---|---|---|
| `start_cpu_profile` | ✅ | Open-ended attach-mode session; returns `sessionId` immediately, sampling continues in background. |
| `stop_cpu_profile` | ✅ | Stops by session id, returns the same summary shape as `profile_cpu`. Double-stop rejected via `SessionRegistry.TakeLive`. |

Demo client: `CpuHogDemo` (sibling repo) connects as an MCP client over stdio and
maps console keys `s`/`x` to these tools against its own pid — see its README.

Known gaps to close (carried into Phase 2 hardening):

- [ ] Live sessions have no auto-stop: a forgotten `start_cpu_profile` samples
      forever and the `.nettrace` grows unboundedly. Add a max-session-age
      watchdog (e.g. auto-stop + warn after 10 min).
- [ ] No cap on concurrent live sessions per target or per server.
- [ ] Live sessions are not disposed on server shutdown (host exits, EventPipe
      session left to the runtime to notice). Hook `IHostApplicationLifetime`.
- [ ] `StopAsync(live, ct)` ignores its `ct` parameter (stop is deliberately
      robust, but the unused parameter is misleading — drop it or honor it).
- [ ] Launch-mode start/stop (start a suspended child, stop later) — deferred;
      attach-mode only today.
- [ ] No automated test for the start/stop path (only the one-shot path is
      covered). Add an integration test mirroring
      `AttachMode_Analyze_BurnCpuDominates`: start BurnerApp → `StartAsync` →
      delay 5 s → `StopAsync` → assert `BurnCpu` dominates.

## Phase 2 — Runtime counters ⬜ (next up)

The `dotnet-counters` equivalent. Lowest-effort/highest-value next step: no
trace parsing at all, just `System.Runtime` EventCounters over EventPipe.

| Tool | Sketch |
|---|---|
| `sample_counters` | ⬜ Attach by pid, collect N seconds of `System.Runtime` counters (plus optional extra providers), return min/max/mean/last per counter: GC heap size by gen, allocation rate, % time in GC, threadpool queue length + thread count, lock contention rate, exception rate, JIT time, assembly count. |
| `watch_counters` (start/stop pair) | ⬜ Open-ended variant reusing the Phase 1.5 live-session plumbing; returns a time series downsampled to fit a context window. |

Design notes:
- Mechanism: `EventPipeProvider("System.Runtime", Informational, args: {"EventCounterIntervalSec": "1"})`,
  consume `EventCounters` events streaming (no file needed) — a second
  `ICollectorAdapter` is wrong here; counters want their own lightweight path.
- Summary model: `CounterSummary { counters: [{name, unit, min, max, mean, last}], durationSeconds, warnings }`.
- This is also the right tool for the "is it CPU, GC, or lock contention?"
  triage question an LLM asks before choosing a deeper tool.

## Phase 3 — Managed heap: gcdump + allocation hotspots ⬜

The `dotnet-gcdump` equivalent (README's original "memory" phase).

| Tool | Sketch |
|---|---|
| `capture_gcdump` | ⬜ `DiagnosticsClient.WriteDump`-adjacent flow via the `Microsoft-DotNETCore-SampleProfiler`-independent gcdump protocol (`EventPipeSession` with `Microsoft-DotNETCore-GCHeapDump` keyword, or reuse `Microsoft.Diagnostics.NETCore.Client` + `graphs`/`MemoryGraph` from TraceEvent). Summary: top N types by retained size and instance count, total heap size, gen sizes. |
| `diff_gcdumps` | ⬜ Two session ids → per-type delta (leak hunting). Needs artifacts kept per session — already the registry's model. |
| `trace_allocations` | ⬜ EventPipe session with `GCSampledObjectAllocation` (low overhead) or `gc-verbose` keywords; summary: top allocating types + allocating call stacks, reusing `CallTree`/`CpuSummaryBuilder` machinery with "allocations" instead of "samples" as the metric. |

Design notes:
- `.gcdump` artifacts join `.nettrace` in `~/.dotnet-profiler-mcp/artifacts/`;
  `SessionRegistry.ResolveTracePath` grows a sibling for `.gcdump`.
- Allocation tracing at `gc-verbose` has real overhead — the tool description
  must say so, and duration should clamp lower (e.g. 1–30 s).

## Phase 4 — Thread stacks ⬜

The `dotnet-stack` equivalent.

| Tool | Sketch |
|---|---|
| `dump_stacks` | ⬜ Instantaneous all-threads managed stack snapshot (short EventPipe sample burst, ~250 ms, then collapse per-thread stacks). Answers "what is this process doing right now / is it deadlocked?" without a full profile. |

Design notes: cheapest deep-dive tool; pairs with `sample_counters` for triage.
Reuses the entire existing collection + parsing pipeline with a tiny duration
and a per-thread (not aggregated) summary.

## Phase 5 — Async / TPL causality ⬜ (README's original phase 3)

| Tool | Sketch |
|---|---|
| `profile_async` | ⬜ EventPipe session with TplEventSource (`System.Threading.Tasks.TplEventSource`, TasksFlowActivityIds) + sample profiler; reconstruct async activity graph, report: longest-running async operations, sync-over-async (`Task.Wait`/`.Result` on threadpool threads), threadpool starvation signals. |

Design notes: hardest summarization problem of the set — the raw activity graph
is huge; the LLM summary should be top offenders per category, not the graph.

## Phase 5.5 — Database queries ⬜ (VS "Database tool" equivalent)

| Tool | Sketch |
|---|---|
| `trace_db_queries` | ⬜ EventPipe session on `Microsoft-Diagnostics-DiagnosticSource` with a filter spec for ADO.NET / EF Core events (plus EF Core EventCounters for rates). Summary: slowest queries, per-query count + total/mean duration. |

Design notes:
- Only observes apps whose data access goes through `DiagnosticSource`-instrumented
  libraries (EF Core, SqlClient, Npgsql) — the tool description must state this.
- Captured SQL text is sensitive; consider a `includeQueryText` opt-in flag
  (default: parameterized shape only).

## Phase 6 — Crash/hang dumps ⬜ (stretch)

The `dotnet-dump` equivalent.

| Tool | Sketch |
|---|---|
| `capture_dump` | ⬜ `DiagnosticsClient.WriteDump(DumpType.WithHeap, path)` — trivial to collect. |
| `analyze_dump` | ⬜ Needs Microsoft.Diagnostics.Runtime (ClrMD): thread stacks, heap stats, sync-block/lock inspection. Big dependency, platform quirks — keep last. |

## Explicitly out of scope

Visual Studio profiling tools that **cannot** be reproduced on the EventPipe /
DiagnosticsClient stack (cross-platform, admin-free) — listed so they don't
creep back in as wishlist items:

- **File I/O** — VS uses ETW kernel file events (Windows-only). No EventPipe
  equivalent; macOS alternatives (`fs_usage`, dtrace) require root.
- **GPU Usage** — ETW + DirectX kernel providers, Windows-only.
- **Instrumentation** — IL rewriting / native `ICorProfiler` attach; a different
  mechanism entirely, not an `ICollectorAdapter`.
- **Application Timeline** — WPF/XAML ETW render events; Windows-only UI
  frameworks, irrelevant to this profiler's targets.

## Cross-cutting work (any phase)

- [ ] `list_dotnet_processes` tool — surface `DiagnosticsClient.GetPublishedProcesses()`
      with process names, so the LLM can find a pid without shell access
      (`DescribeDiscoverableProcesses` already exists as the error hint; promote it to a tool).
- [ ] `list_sessions` tool — enumerate artifacts dir + live sessions.
- [ ] Artifact retention policy (age- or size-based cleanup of `~/.dotnet-profiler-mcp/artifacts/`).
- [ ] README section per tool as each phase lands (keep tool table current).

---

## Progress log

| Date | What shipped |
|---|---|
| 2026-06 | Phase 0+1: `profile_cpu`, `get_hot_paths`, attach + launch modes, error taxonomy, 17-test suite. |
| 2026-07-05 | Phase 1.5: `start_cpu_profile` / `stop_cpu_profile` (attach-mode live sessions), `LiveCpuSession`, `SessionRegistry.RegisterLive/TakeLive`; CpuHogDemo became an interactive MCP-client demo (`s`/`x` keys). All existing tests still green. |
| 2026-07-05 | Review fixes: `StartAsync` no longer leaks the EventPipe session on late failure; shared `CpuProviders()`; start/stop integration test added (suite now 18). Windows compat: launch-mode diagnostic port uses a named pipe on Windows; CpuHogDemo resolves `dotnet.exe`/PATH. CpuHogDemo hardening: redirected-stdin guard, graceful server-start failure, less console noise in its own profile. |
