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
- When no job is pending and no active discovery session exists: crawl the customer's SQL Server and post discovered objects to the backend (`RunDiscovery`)
- When a job arrives: start the job, fetch its objects and parameter sets, capture estimated execution plans, post them for Claude optimization
- Poll for Claude's optimized results
- Deploy each optimized object temporarily under an `[optimizer]` schema
- Execute both the original and optimized versions with each parameter set under `SET STATISTICS IO/TIME/XML ON` to capture real execution metrics
- Clean up the `[optimizer]` schema copy
- Post execution metrics back to the backend

**Data that leaves the customer network:** Only T-SQL object definitions (code), execution plan XML (structure), and numeric metrics (row counts, execution time, logical reads, CPU time). No row data, no PII. AI-generated parameter sets contain SQL sampling queries, not actual data values — those queries are resolved against the live database locally.

---

## Solution Structure

```
dboptimizer-agent/
  DbOptimizer.Agent/            — .NET Worker Service (the agent itself)
    Configuration/
      AgentConfiguration.cs     — Typed config: AgentId, BackendUrl, ApiKey, SqlConnectionString, poll intervals
    Crawling/
      SqlServerCrawler.cs       — Crawls sys.objects/sys.sql_modules, queries DMVs for frequencies
      SqlObjectExecutor.cs      — Deploys to [optimizer] schema, executes, captures metrics, resolves $query params
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

- **`DbOptimizer.Contracts`** — NuGet package from the backend repo. Provides all shared DTOs (`JobDto`, `JobObjectDto`, `ParameterSetDto`, `DiscoveredObjectDto`, `ExecutionResultDto`, `BaselineObjectResult`), request types, and `AgentPollResponse`. This is the only cross-repo dependency.
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
1. `TrySendHeartbeatAsync` — sends `POST /api/agent/heartbeat` if more than `HeartbeatIntervalSeconds` have passed. Caches server name, database name, SQL Server version, and compatibility level from the first successful `GetServerInfoAsync` call.
2. Build `AgentPollRequest` with version and masked server/DB metadata.
3. `BackendApiClient.PollForJobAsync` — `POST /api/agent/poll` with the request body. Returns `null` on failure or `204 NoContent`.
4. If null → sleep `PollIntervalSeconds` and loop.
5. Check `poll.MustUpdate` — if true, log critical and **stop the service**.
6. Check `poll.UpdateAvailable` — if true, log a warning and continue.
7. If `poll.RunDiscovery` → `RunDiscoveryAsync` (crawl and post to backend), then `continue`.
8. If `poll.PendingJobs.Count > 0` → `ProcessJobAsync(poll.PendingJobs[0])`.
9. Else if `poll.ReadyToExecuteObjects.Count > 0` → `SubmitOptimizedMetricsAsync` for each (handles objects picked up via BenchmarkingComplete poll path).
10. Else → sleep and loop.

### `RunDiscoveryAsync`

Called when `poll.RunDiscovery == true` (backend has no active discovery session for this agent).

1. `SqlServerCrawler.CrawlObjectsAsync` → `List<DiscoveredObjectDto>`
2. `BackendApiClient.PostDiscoveryAsync` → `POST /api/agent/discovery`
3. Backend creates a `DiscoverySession`, fires `DiscoverySessionCreated` SignalR event to navigate the dashboard.
4. User selects objects in the dashboard to create a job — agent does NOT participate further in discovery.

### `ProcessJobAsync`

Three sequential phases for each job:

**Phase 1 — Baseline execution plans**
1. `BackendApiClient.StartJobAsync` → `POST /api/agent/jobs/{jobId}/start` — transitions job `Pending → Running`.
2. `BackendApiClient.GetJobObjectsAsync` → `GET /api/agent/jobs/{jobId}/objects` — returns all non-skipped objects with their parameter sets.
3. `RunBaselineExecutionsAsync` — for each object, calls `SqlObjectExecutor.GetEstimatedPlanAsync` (uses `SET SHOWPLAN_XML ON` — no actual execution, no parameters needed).
4. Successful plan captures are batched and posted via `BackendApiClient.SubmitBaselinesAsync` → `POST /api/agent/jobs/{jobId}/baselines`.
5. Objects that fail plan capture are reported via `BackendApiClient.ReportExecutionFailedAsync`.
6. The backend stores the plan XML on each `JobObject` and transitions them to `Optimizing` so the orchestration service calls Claude with full execution plan context.

**Phase 2 — Poll for optimization results**
`PollForResultsWithTimeoutAsync` → `GET /api/agent/jobs/{jobId}/results` every **10 seconds**, **30-minute timeout**.
- Returns `null` when backend responds `204 NoContent` (not ready) or `200 OK` with empty list (treated the same).
- Returns the list once objects with optimized definitions are ready.

**Phase 3 — Benchmark optimized versions**
For each `JobObjectDto` in the results: `SubmitOptimizedMetricsAsync`.

### `SubmitOptimizedMetricsAsync`

Executes both original and optimized versions for every parameter set and posts all results together.

1. Determine parameter sets to use — `obj.ParameterSets` if present, otherwise a synthetic empty set.
2. Deploy optimized version once: `SqlObjectExecutor.DeployUnderOptimizerSchemaAsync` (reused for all param sets).
3. For each parameter set:
   - **Original execution:** `SqlObjectExecutor.ExecuteAndCaptureAsync(obj, parametersJson)` → `ExecutionResultDto` with `ExecutionVersionId = 1`.
   - If original fails → `continue` to next param set (optimized skipped for this set).
   - **Optimized execution:** `SqlObjectExecutor.ExecuteAndCaptureAsync(optimizerObj with SchemaName="optimizer", parametersJson)` → `ExecutionResultDto` with `ExecutionVersionId = 2`. Non-fatal — if optimized fails, original metrics are still posted.
4. Cleanup: `SqlObjectExecutor.RemoveFromOptimizerSchemaAsync` in `finally`.
5. If `results.Count == 0` → call `ReportExecutionFailedAsync` (triggers credit refund).
6. `BackendApiClient.SubmitMetricsAsync` → `POST /api/agent/jobs/{jobId}/metrics`.
7. If submit fails → call `ReportExecutionFailedAsync`.

> **`SubmitObjectMetricsAsync`** (single-param-set version) still exists in `AgentWorker.cs` but is **dead code** — it is not called anywhere. The current path always goes through `SubmitOptimizedMetricsAsync`.

---

## `$query` Convention in Parameter Sets

Parameter sets can contain AI-generated sampling queries instead of literal values. Claude writes these to avoid hardcoding specific data values.

**Format in `ParameterSet.ParametersJson`:**
```json
{"@CustomerId": {"$query": "SELECT TOP 1 CustomerId FROM Customers ORDER BY NEWID()"}}
```

**`ResolveParametersAsync`** in `SqlObjectExecutor` detects this convention:
- Iterates the JSON object properties.
- If a value is an object containing `"$query"`, executes that SQL against the live database.
- Uses the first column of the first row as the resolved value.
- On failure (query error, null result) — substitutes `NULL` and logs a warning.
- Non-`$query` values are parsed normally (number, string, bool, etc.).

This ensures no actual data values ever reach Claude or the backend — all resolution happens inside the customer's network.

---

## `SqlServerCrawler`

Connects to the customer SQL Server and returns all crawlable objects.

**`CrawlObjectsAsync` flow:**
1. `GetServerInfoAsync` — `@@SERVERNAME`, `DB_NAME()`, `@@VERSION`, `CompatibilityLevel` via `DATABASEPROPERTYEX`. Returns a `ServerInfo` record cached on the worker.
2. `GetProcedureFrequenciesAsync` — queries `sys.dm_exec_procedure_stats` for estimated executions per day (`SUM(execution_count) / DATEDIFF(DAY, cached_time, GETDATE())`). Only covers stored procedures. Silently swallows `SqlException` (e.g. `VIEW SERVER STATE` permission missing on SQL Express).
3. Main query: `sys.objects JOIN sys.sql_modules JOIN sys.schemas` for types `P, V, FN, IF, TF`, excluding `is_ms_shipped = 1` objects.
4. For each object: maps `sys.objects.type` code to `ObjectTypeId` (inlined constants — Core not referenced), looks up frequency, runs `ExtractHints`, crawls parameter metadata via `sys.parameters` for stored procedures, builds a `DiscoveredObjectDto`.

**Object type codes → ObjectTypeId:**
| sys.objects.type | ObjectTypeId |
|-----------------|-------------|
| `P` | 1 (StoredProcedure) |
| `V` | 2 (View) |
| `FN` | 3 (ScalarFunction) |
| `IF` | 4 (TableValuedFunction) |
| `TF` | 5 (MultiStatementTVF) |

> These constants are inlined in the crawler rather than imported from `DbOptimizer.Core`, because the agent does not reference Core. They must stay in sync with `ObjectTypeIds` in the backend.

**Parameter metadata** is crawled from `sys.parameters` for stored procedures and stored in `DiscoveredObjectDto.Parameters`. The backend's `ParameterOptionalityParser` determines which are optional from the SP definition. This JSON ends up in `JobObject.ParametersJson` and is used by the AI parameter generation feature.

---

## `SqlObjectExecutor`

Executes SQL objects and captures performance metrics.

### `GetEstimatedPlanAsync`

Uses `SET SHOWPLAN_XML ON` to get the estimated execution plan without executing the object. No parameters are needed — SQL Server compiles and returns the plan. Returns plan XML string or `null` on failure. Used in Phase 1 (baseline plan capture).

### `ExecuteAndCaptureAsync`

Resolves `$query` parameters first via `ResolveParametersAsync`, then runs `SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;` and executes the object. Collects:
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
2. `RewriteObjectHeader` — replaces the first `CREATE [OR ALTER] PROCEDURE/FUNCTION/VIEW [schema].[name]` with `CREATE OR ALTER {KEYWORD} [optimizer].[name]` using a compiled regex.
3. Executes the rewritten DDL.

### `RemoveFromOptimizerSchemaAsync`

Runs `DROP {PROCEDURE|VIEW|FUNCTION} IF EXISTS [optimizer].[name]`. Always called in `finally` after optimized execution.

---

## `BackendApiClient`

Thin HTTP client. `X-Agent-ApiKey` is added to `DefaultRequestHeaders` at construction. All methods swallow non-cancellation exceptions and return `null`/`false` on failure — the agent's job loop treats these as transient failures and continues.

| Method | Endpoint | Returns null/false when |
|--------|----------|------------------------|
| `PollForJobAsync` | `POST /api/agent/poll` | Any exception, or `204 NoContent` |
| `PostDiscoveryAsync` | `POST /api/agent/discovery` | Any exception |
| `StartJobAsync` | `POST /api/agent/jobs/{id}/start` | Any exception |
| `GetJobObjectsAsync` | `GET /api/agent/jobs/{id}/objects` | Any exception |
| `SubmitBaselinesAsync` | `POST /api/agent/jobs/{id}/baselines` | Any exception |
| `PollForResultsAsync` | `GET /api/agent/jobs/{id}/results` | Any exception, `204 NoContent`, or `200 []` |
| `SubmitMetricsAsync` | `POST /api/agent/jobs/{id}/metrics` | Any exception |
| `ReportExecutionFailedAsync` | `POST /api/agent/jobs/{id}/objects/{oid}/execution-failed` | Any exception |
| `SendHeartbeatAsync` | `POST /api/agent/heartbeat` | Any exception |

---

## Job Processing Flow (Agent's Perspective)

```
[idle]
  ↓ poll interval
POST /api/agent/poll  (body: AgentPollRequest with version + masked server metadata)
  ├─ MustUpdate=true       →  stop service
  ├─ RunDiscovery=true     →  CrawlObjectsAsync → POST /api/agent/discovery → continue
  ├─ PendingJobs[0] = job J  →  ProcessJobAsync(J)
  │     ↓
  │   POST /api/agent/jobs/J/start
  │     ↓
  │   GET /api/agent/jobs/J/objects  (returns objects + parameter sets)
  │     ↓
  │   Phase 1: RunBaselineExecutionsAsync
  │     for each object: GetEstimatedPlanAsync (SET SHOWPLAN_XML ON — no actual execution)
  │     POST /api/agent/jobs/J/baselines  (plan XMLs)
  │       backend: stores plan XML on JobObject, transitions to Optimizing
  │       JobOrchestrationService: calls Claude with plan context
  │     ↓
  │   Phase 2: PollForResultsWithTimeoutAsync  (every 10s, 30min timeout)
  │     GET /api/agent/jobs/J/results
  │     → 204 or 200+[] → keep polling
  │     → 200+objects   → proceed to Phase 3
  │     ↓
  │   Phase 3: SubmitOptimizedMetricsAsync for each optimized object
  │     DeployUnderOptimizerSchemaAsync (once per object)
  │     for each ParameterSet:
  │       ExecuteAndCaptureAsync(original)  → ExecutionVersionId=1
  │       ExecuteAndCaptureAsync([optimizer].[name])  → ExecutionVersionId=2
  │     RemoveFromOptimizerSchemaAsync
  │     POST /api/agent/jobs/J/metrics
  │     (if all param sets failed: POST /api/agent/jobs/J/objects/{id}/execution-failed)
  │     ↓
  │   [job complete for this agent]
  │
  ├─ ReadyToExecuteObjects.Count > 0  →  SubmitOptimizedMetricsAsync for each
  │   (handles BenchmarkingComplete jobs picked up via poll without a pending job)
  │
  └─ nothing pending  →  sleep PollIntervalSeconds, loop
```

---

## Heartbeat Behavior

- Heartbeats fire when `DateTime.UtcNow - _lastHeartbeat >= HeartbeatIntervalSeconds`.
- They are sent at the **start** of every poll cycle and **opportunistically** while waiting for results.
- The poll endpoint also records a heartbeat inline on the backend, so `POST /api/agent/heartbeat` is supplementary.
- A heartbeat failure does not abort the current job — the agent logs a warning and continues.

---

## Version Enforcement

The agent sends its version (`Assembly.GetExecutingAssembly().GetName().Version`) in the `AgentPollRequest` body on every poll. The backend can respond with `MustUpdate=true` to force the agent to stop. Currently the backend never sets this flag, but the wiring is in place.

---

## Developer Notes

- **No ORM.** All SQL is raw ADO.NET. `SqlServerCrawler` and `SqlObjectExecutor` each create their own `SqlConnection` per operation.
- **ObjectTypeId constants are inlined** in `SqlServerCrawler.ObjectTypeMap` because the agent doesn't reference `DbOptimizer.Core`. If you add a new object type to the backend, update both `ObjectTypeIds` in Core and the map here.
- **Regex for hint extraction** uses two patterns: one for `OPTION(...)` and one for `WITH(...)`. The WITH pattern explicitly lists recognized hint names to avoid matching CTE syntax (`WITH cte AS ...`).
- **Execution plan XML** is identified by the result set column name containing "XML Showplan" — this is SQL Server's convention for the Statistics XML result set.
- **Missing index suggestions** are parsed from `<MissingIndexGroup>` elements in the plan XML.
- **CommandTimeout**: 120s for crawl queries, 120s for execution, 60s for deploy, 30s for drop.
- **The `[optimizer]` schema** is created in the customer database if absent. Used as a sandbox for running the optimized version. Always cleaned up in `finally`.
- **Server/database names** are masked before being sent to the backend: first char + last 3 chars, rest replaced with `*`. A SHA-256 hash of `serverName|databaseName` (lowercased) is also sent for identity matching without revealing the full names.
- **`SubmitObjectMetricsAsync`** in `AgentWorker.cs` is dead code — it is never called. The active path is `SubmitOptimizedMetricsAsync`.
