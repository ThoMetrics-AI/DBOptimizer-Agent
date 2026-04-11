# CLAUDE.md — dboptimizer-agent (On-Premise Agent)

## Build & Run

```bash
# Build
dotnet build

# Run locally (requires appsettings.json with valid Agent config)
dotnet run --project DbOptimizer.Agent

# Publish self-contained Windows x64 binary (used by installer)
dotnet publish DbOptimizer.Agent -c Release -r win-x64 --self-contained
```

The agent runs as a Windows Service in production. It is installed by `DbOptimizer.Agent.Installer` (Inno Setup).

---

## What This Repo Does

This is the on-premise component of DbOptimizer. It runs inside the customer's firewall as a Windows Service and communicates outbound only — it never accepts inbound connections.

**Responsibilities:**
- Poll the DbOptimizer backend API for new optimization jobs
- Connect to the customer's SQL Server and crawl stored procedures, views, and functions
- Post discovered object definitions to the backend (which triggers Claude classification and optimization)
- Poll for Claude's optimized results
- Deploy each optimized object temporarily under an `[optimizer]` schema
- Execute both the original and optimized versions under `SET STATISTICS IO/TIME/XML ON` to capture real execution metrics
- Clean up the `[optimizer]` schema copy
- Post execution metrics back to the backend

**Data that leaves the customer network:** Only T-SQL object definitions (code), execution plan XML (structure), and numeric metrics (row counts, execution time, logical reads, CPU time). No row data, no PII.

---

## Solution Structure

```
dboptimizer-agent/
  DbOptimizer.Agent/            — .NET Worker Service (the agent itself)
    Configuration/
      AgentConfiguration.cs     — Typed config: AgentId, BackendUrl, ApiKey, SqlConnectionString, poll intervals
    Crawling/
      SqlServerCrawler.cs       — Crawls sys.objects/sys.sql_modules, queries DMVs for frequencies
      SqlObjectExecutor.cs      — Deploys to [optimizer] schema, executes, captures metrics
    Http/
      BackendApiClient.cs       — All HTTP calls to the backend API
    Worker/
      AgentWorker.cs            — Main BackgroundService poll loop
    Program.cs                  — Host setup: registers SqlServerCrawler, SqlObjectExecutor, BackendApiClient, AgentWorker

  DbOptimizer.Agent.Installer/  — Inno Setup project
                                   Wraps the published binary into a Windows .exe installer,
                                   registers as Windows Service, prompts for API key and connection string.
```

### External dependencies

- **`DbOptimizer.Contracts`** — NuGet package from the backend repo. Provides all shared DTOs (`JobDto`, `JobObjectDto`, `DiscoveredObjectDto`, `ExecutionResultDto`), request types, and `AgentPollResponse`. This is the only cross-repo dependency.
- **`Microsoft.Data.SqlClient`** — Direct ADO.NET for all SQL Server access. No ORM.
- No reference to `DbOptimizer.Core`, `DbOptimizer.Claude`, or `DbOptimizer.Infrastructure`.

### Target framework

`net10.0` (the backend is `net8.0`). Both must be able to serialize/deserialize `DbOptimizer.Contracts` types — keep the contract package targeting a common TFM or use `netstandard2.0`.

---

## Configuration

All settings live in `appsettings.json` under the `"Agent"` section (`AgentConfiguration.SectionName`):

| Key | Purpose | Default |
|-----|---------|---------|
| `AgentId` | Numeric ID assigned by backend at registration | — |
| `BackendUrl` | Base URL of the SaaS API, e.g. `https://api.dboptimizer.com` | — |
| `ApiKey` | API key issued on registration. Sent as `X-Agent-ApiKey` header | — |
| `SqlConnectionString` | Connection string to the customer SQL Server | — |
| `PollIntervalSeconds` | How often to poll for new jobs (when idle) | 15 |
| `HeartbeatIntervalSeconds` | Min interval between heartbeat sends | 60 |
| `HttpTimeoutSeconds` | Per-request HTTP timeout to backend | 30 |

The installer writes `AgentId`, `BackendUrl`, `ApiKey`, and `SqlConnectionString` on first run. Other values use defaults.

---

## Core Components

### `AgentWorker` (BackgroundService)

The top-level poll loop. Runs continuously until cancelled.

**Each iteration:**
1. `TrySendHeartbeatAsync` — sends `POST /api/agent/heartbeat` if more than `HeartbeatIntervalSeconds` have passed since the last successful heartbeat. Includes `AgentId`, `AgentVersion` (from assembly version), and `MachineName`.
2. `BackendApiClient.PollForJobAsync` — calls `GET /api/agent/poll?agentVersion=X.Y.Z`. Returns `null` if the HTTP response is `204 NoContent`.
3. If null → sleep `PollIntervalSeconds` and loop.
4. Check `poll.MustUpdate` — if true, log critical and **stop the service**. The agent must be updated before it can work again.
5. Check `poll.UpdateAvailable` — if true, log a warning and continue.
6. If `poll.PendingJobs.Count > 0` → process the first job (`ProcessJobAsync`). Does not sleep — loops immediately after.
7. If no pending jobs → sleep and loop.

> **Note:** `poll.ReadyToExecuteObjects` from the poll response is currently not consumed. The agent processes execution work via the per-job `/results` polling endpoint inside `ProcessJobAsync`.

### `ProcessJobAsync`

Four sequential steps for each job:

**Step 1 — Crawl**
`SqlServerCrawler.CrawlObjectsAsync` → returns `List<DiscoveredObjectDto>`.

**Step 2 — Submit definitions**
`BackendApiClient.SubmitObjectDefinitionsAsync` → `POST /api/agent/jobs/{jobId}/definitions`. This call triggers Layer 1 classification and queues Claude work on the backend. On failure (non-2xx), the job is abandoned and the agent returns to polling.

**Step 3 — Poll for results**
`PollForResultsWithTimeoutAsync` → calls `GET /api/agent/jobs/{jobId}/results` every **10 seconds** with a **30-minute timeout**. Returns `null` on timeout. Sends heartbeats opportunistically while waiting.

> **Known bug:** The backend endpoint always returns `200 OK` with `[]` rather than `204 NoContent` when no results are ready. `PollForResultsAsync` only returns `null` for `204`. This means the agent breaks out of the result-poll loop immediately on the first attempt (receiving an empty list, not null), skips all metrics submission, and logs "received 0 optimized objects". See the backend CLAUDE.md for the fix.

**Step 4 — Execute and post metrics**
For each `JobObjectDto` in the results list: calls `SubmitObjectMetricsAsync`.

### `SubmitObjectMetricsAsync`

For each object with an optimized definition:

1. Determine the parameter set to use — picks the `IsDefault` parameter set, or the first one, or an empty parameter set if none exist.
2. **Execute original:** `SqlObjectExecutor.ExecuteAndCaptureAsync(obj, parametersJson)` — runs against the original schema. Creates an `ExecutionResultDto` with `ExecutionVersionId = 1 (Original)`.
3. If `OptimizedDefinition` is present:
   - `SqlObjectExecutor.DeployUnderOptimizerSchemaAsync` — creates the `[optimizer]` schema if absent, rewrites the object's CREATE/ALTER header to target `[optimizer].[name]`, deploys with `CREATE OR ALTER`.
   - `SqlObjectExecutor.ExecuteAndCaptureAsync` on the `[optimizer]` schema copy. Creates an `ExecutionResultDto` with `ExecutionVersionId = 2 (Optimized)`.
   - `SqlObjectExecutor.RemoveFromOptimizerSchemaAsync` — drops `[optimizer].[name]`. Always runs (in `finally`), even if optimized execution failed.
4. `BackendApiClient.SubmitMetricsAsync` → `POST /api/agent/jobs/{jobId}/metrics`.

If the **original** execution fails, the object is skipped entirely (no metrics posted, no optimized execution attempted).  
If the **optimized** execution fails, the original metrics are still posted.

---

## `SqlServerCrawler`

Connects to the customer SQL Server and returns all crawlable objects.

**`CrawlObjectsAsync` flow:**
1. `GetServerInfoAsync` — `@@SERVERNAME`, `DB_NAME()`, `@@VERSION`, `CompatibilityLevel` via `DATABASEPROPERTYEX`.
2. `GetProcedureFrequenciesAsync` — queries `sys.dm_exec_procedure_stats` for estimated executions per day (`SUM(execution_count) / DATEDIFF(DAY, cached_time, GETDATE())`). Only covers stored procedures — views and functions are not in this DMV. Silently swallows `SqlException` (e.g. `VIEW SERVER STATE` permission missing on SQL Express).
3. Main query: `sys.objects JOIN sys.sql_modules JOIN sys.schemas` for types `P, V, FN, IF, TF`, excluding `is_ms_shipped = 1` objects.
4. For each object: maps the `sys.objects.type` code to an `ObjectTypeId` constant (inlined — Core is not referenced), looks up frequency from the DMV results, runs `ExtractHints` to find `OPTION(...)` and `WITH(...)` clauses via regex, builds a `DiscoveredObjectDto`.

**Object type codes → ObjectTypeId:**
| sys.objects.type | ObjectTypeId |
|-----------------|-------------|
| `P` | 1 (StoredProcedure) |
| `V` | 2 (View) |
| `FN` | 3 (ScalarFunction) |
| `IF` | 4 (TableValuedFunction) |
| `TF` | 5 (MultiStatementTVF) |

> These constants are inlined in the crawler rather than imported from `DbOptimizer.Core`, because the agent does not reference Core. They must stay in sync with `ObjectTypeIds` in the backend.

---

## `SqlObjectExecutor`

Executes SQL objects and captures performance metrics.

### `ExecuteAndCaptureAsync`

Runs `SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;`, then executes the object. Collects:
- `ExecutionMs` — wall-clock time via `Stopwatch`
- `LogicalReads` — summed from `InfoMessage` events via regex (`logical reads (\d+)`)
- `CpuTimeMs` — summed from `InfoMessage` events via regex (`CPU time = (\d+) ms`)
- `RowsReturned` — counted by iterating all non-showplan result sets
- `ExecutionPlanXml` — detected by result set with a column name containing "XML Showplan"
- `MissingIndexSuggestions` — parsed from `<MissingIndexGroup>` elements in the plan XML

**Execution by object type:**
| Type | Execution |
|------|-----------|
| StoredProcedure | `SqlCommand` with `CommandType.StoredProcedure` + named parameters |
| View | `SELECT * FROM [schema].[name]` |
| ScalarFunction | `SELECT [schema].[name](@p0, @p1, ...)` |
| InlineTVF / MultiStatementTVF | `SELECT * FROM [schema].[name](@p0, @p1, ...)` |

### `DeployUnderOptimizerSchemaAsync`

1. Ensures the `[optimizer]` schema exists (`IF NOT EXISTS ... EXEC('CREATE SCHEMA [optimizer]')`).
2. `RewriteObjectHeader` — replaces the first `CREATE [OR ALTER] PROCEDURE/FUNCTION/VIEW [schema].[name]` in the definition with `CREATE OR ALTER {KEYWORD} [optimizer].[name]` using a compiled regex.
3. Executes the rewritten DDL as a `SqlCommand`.

### `RemoveFromOptimizerSchemaAsync`

Runs `DROP {PROCEDURE|VIEW|FUNCTION} IF EXISTS [optimizer].[name]`. Always called in `finally` after optimized execution.

---

## `BackendApiClient`

Thin HTTP client. `X-Agent-ApiKey` is added to `DefaultRequestHeaders` at construction. All methods swallow non-cancellation exceptions and return `null`/`false` on failure — the agent's job loop treats these as transient failures and continues.

| Method | Endpoint | Returns null/false when |
|--------|----------|------------------------|
| `PollForJobAsync` | `GET /api/agent/poll?agentVersion=X` | Any exception, or `204 NoContent` |
| `SubmitObjectDefinitionsAsync` | `POST /api/agent/jobs/{id}/definitions` | Any exception |
| `PollForResultsAsync` | `GET /api/agent/jobs/{id}/results` | Any exception, or `204 NoContent` |
| `SubmitMetricsAsync` | `POST /api/agent/jobs/{id}/metrics` | Any exception |
| `SendHeartbeatAsync` | `POST /api/agent/heartbeat` | Any exception |

---

## Job Processing Flow (Agent's Perspective)

```
[idle]
  ↓ poll interval
GET /api/agent/poll
  ├─ MustUpdate=true  →  stop service
  ├─ no pending jobs  →  sleep, loop
  └─ pending jobs[0] = job J
        ↓
CrawlObjectsAsync()
  → N DiscoveredObjectDtos
        ↓
POST /api/agent/jobs/J/definitions
  → backend transitions job to Running
  → backend runs Layer 1 classification
  → NeedsReview objects → Classifying
  → ReadOnly objects    → Optimizing
  → HasWrites/Dynamic   → Skipped
  → JobOrchestrationService picks up Classifying/Optimizing objects
    and calls Claude (runs independently in backend)
        ↓
[poll loop] GET /api/agent/jobs/J/results  (every 10s, 30min timeout)
  → returns non-null list when AwaitingApproval objects exist
        ↓
for each optimized JobObject:
  ExecuteAndCaptureAsync(original)
  DeployUnderOptimizerSchemaAsync(optimized)
  ExecuteAndCaptureAsync([optimizer].[name])
  RemoveFromOptimizerSchemaAsync
  POST /api/agent/jobs/J/metrics
        ↓
[idle] loop
```

---

## Heartbeat Behavior

- Heartbeats fire when `DateTime.UtcNow - _lastHeartbeat >= HeartbeatIntervalSeconds`.
- They are sent at the **start** of every poll cycle and also **opportunistically** while waiting for results.
- The poll endpoint (`GET /api/agent/poll`) also records a heartbeat inline on the backend side, so heartbeat calls via `POST /api/agent/heartbeat` are supplementary.
- A heartbeat failure does not abort the current job — the agent logs a warning and continues.

---

## Version Enforcement

The agent sends its version (`Assembly.GetExecutingAssembly().GetName().Version`) as a query parameter on every poll. The backend can respond with `MustUpdate=true` to force the agent to stop. Currently the backend never sets this flag, but the wiring is in place.

---

## Developer Notes

- **No ORM.** All SQL is raw ADO.NET. `SqlServerCrawler` and `SqlObjectExecutor` each create their own `SqlConnection` per operation.
- **ObjectTypeId constants are inlined** in `SqlServerCrawler.ObjectTypeMap` because the agent doesn't reference `DbOptimizer.Core`. If you add a new object type to the backend, update both `ObjectTypeIds` in Core and the map here.
- **Regex for hint extraction** uses two patterns: one for `OPTION(...)` and one for `WITH(...)`. The WITH pattern explicitly lists recognized hint names to avoid matching CTE syntax (`WITH cte AS ...`).
- **Execution plan XML** is identified by the result set column name containing "XML Showplan" — this is SQL Server's convention for the Statistics XML result set.
- **Missing index suggestions** are parsed from `<MissingIndexGroup>` elements in the plan XML, extracting equality/inequality/include columns and impact percentage.
- **CommandTimeout** is 120 seconds for crawl queries, 120 seconds for execution, 60 seconds for deploy, and 30 seconds for drop.
- **The `[optimizer]` schema** is created in the customer database if absent. It is used as a sandbox for running the optimized version without touching the original object. Always cleaned up in `finally`.
- **Parameters** are stored as JSON in `ParameterSet.ParametersJson`. The executor parses the JSON and maps JSON value kinds to CLR types. Unknown value kinds are passed as their raw JSON text.
