# CLAUDE.md — dboptimizer-agent (On-Premise Agent)

## Do NOT Add or Update Tests

Do not create, modify, or add unit tests, integration tests, or any test files. The codebase is in active feature development — tests will be written later as a dedicated pass with complete code coverage. This applies even if a code change would normally warrant a test update.

---

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
- Deploy each optimized object temporarily under a per-job staging schema (`dbopt_XXXXXX`)
- Execute both the original and optimized versions with each parameter set under `SET STATISTICS IO/TIME/XML ON` to capture real execution metrics
- Clean up the staging schema after all objects in the job complete
- Post execution metrics back to the backend

**Data that leaves the customer network:** Only T-SQL object definitions (code), execution plan XML (structure), and numeric metrics (row counts, execution time, logical reads, CPU time). No row data, no PII. AI-generated parameter sets contain SQL sampling queries, not actual data values — those queries are resolved against the live database locally.

---

## Solution Structure

```
dboptimizer-agent/
  DbOptimizer.Agent/            — .NET Worker Service (the agent itself)
    Configuration/
      AgentConfiguration.cs     — Typed config: AgentId, BackendUrl, ApiKey, SqlConnectionString, poll intervals, ChecksumRowThreshold, FloatEpsilon
    Crawling/
      SqlServerCrawler.cs       — Crawls sys.objects/sys.sql_modules, queries DMVs for frequencies; GetSchemaNames() queries sys.schemas
      SqlObjectExecutor.cs      — Deploys to staging schema, executes, captures metrics, resolves $query params
      ResultSetHasher.cs        — Checksum computation for result sets; supports sampled hashing for large sets
      ExecutionValidation.cs    — Row count and column schema comparison between original and optimized executions
    Http/
      BackendApiClient.cs       — All HTTP calls to the backend API
      AgentAuthException.cs     — Exception for 401/403 responses; carries ErrorCode (InvalidApiKey, AgentDisabled, OrgSuspended)
    Worker/
      AgentWorker.cs            — Main BackgroundService poll loop
    Program.cs                  — Host setup: registers SqlServerCrawler, SqlObjectExecutor, BackendApiClient, AgentWorker

  DbOptimizer.Agent.Installer/  — Inno Setup project
                                   Wraps the published binary into a Windows .exe installer,
                                   registers as Windows Service, prompts for API key and connection string.
```

### External dependencies

- **`SqlBrain.Contracts`** (currently v1.0.22) — Public NuGet package published from the backend's `DbOptimizer.Contracts` project. Contains **only agent-consumed types**: `JobDto`, `JobObjectDto`, `ParameterSetDto`, `DiscoveredObjectDto`, `DiscoveredParameterDto`, `ExecutionResultDto`, `BaselineObjectResult`, request types, and `AgentPollResponse`. CRM/billing/proposal DTOs are intentionally excluded (they live in `DbOptimizer.Api/Dtos/Crm/` in the backend) to keep sensitive business data out of this public package. This is the only cross-repo dependency. **Bump the version in `DbOptimizer.Agent.csproj` whenever any file in `DbOptimizer.Contracts` changes.**
- **`Microsoft.Data.SqlClient`** — Direct ADO.NET for all SQL Server access. No ORM.
- No reference to `DbOptimizer.Core`, `DbOptimizer.Claude`, or `DbOptimizer.Infrastructure`.

### Target framework

`net10.0` (the backend is `net8.0`). Both must be able to serialize/deserialize `DbOptimizer.Contracts` types — keep the contract package targeting a common TFM or use `netstandard2.0`.

---

## Configuration

All settings live in `appsettings.json` under the `"Agent"` section (`AgentConfiguration.SectionName`):

| Key | Purpose | Code Default | appsettings.json |
|-----|---------|-------------|-----------------|
| `BackendUrl` | Base URL of the SaaS API, e.g. `https://api.sqlbrain.ai` | — | placeholder |
| `ApiKey` | API key issued on registration. Sent as `X-Agent-ApiKey` header | — | placeholder |
| `SqlConnectionString` | Connection string to the customer SQL Server | — | placeholder |
| `PollIntervalSeconds` | How often to poll for new jobs (when idle) | 15 | **10** |
| `HeartbeatIntervalSeconds` | Min interval between heartbeat sends | 60 | **30** |
| `HttpTimeoutSeconds` | Per-request HTTP timeout to backend | 30 | 30 |
| `ChecksumRowThreshold` | Max rows before switching to sampled checksum | 10,000 | — |
| `FloatEpsilon` | Tolerance for comparing floating-point result values | 0.0001 | — |

The installer writes `BackendUrl`, `ApiKey`, and `SqlConnectionString` on first run. `appsettings.json` in the repo overrides poll and heartbeat intervals to be more aggressive than the code defaults.

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
9. Else if `poll.ReadyToExecuteObjects.Count > 0` → group objects by `JobId`, then `SubmitOptimizedMetricsAsync` for each, with a job-level `finally` that drops the staging schema.
10. Else → sleep and loop.

### `RunDiscoveryAsync`

Called when `poll.RunDiscovery == true` (backend has no active discovery session for this agent).

1. `SqlServerCrawler.CrawlObjectsAsync` → `List<DiscoveredObjectDto>`
2. `SqlServerCrawler.GetSchemaNames()` → `List<string>` — all existing schema names on the customer database (used for staging schema collision detection at the backend).
3. `BackendApiClient.PostDiscoveryAsync(jobId, objects, schemaNames)` → `POST /api/agent/discovery`
4. Backend creates a `DiscoverySession`, performs staging schema collision check, fires `DiscoverySessionCreated` SignalR event to navigate the dashboard.
5. User selects objects in the dashboard — agent does NOT participate further in discovery.

### `ProcessJobAsync`

Three sequential phases for each job:

**Phase 1 — Baseline execution plans**
1. `BackendApiClient.StartJobAsync` → `POST /api/agent/jobs/{jobId}/start` — transitions job `Pending → Running`.
2. `BackendApiClient.GetJobObjectsAsync` → `GET /api/agent/jobs/{jobId}/objects` — returns all non-skipped objects with their parameter sets. Each `JobObjectDto` includes `StagingSchema`.
3. `RunBaselineExecutionsAsync` — for each object, calls `SqlObjectExecutor.GetEstimatedPlanAsync` (uses `SET SHOWPLAN_XML ON` — no actual execution, no parameters needed).
4. Successful plan captures are batched and posted via `BackendApiClient.SubmitBaselinesAsync` → `POST /api/agent/jobs/{jobId}/baselines`.
5. Objects that fail plan capture are reported via `BackendApiClient.ReportExecutionFailedAsync`.
6. The backend stores the plan XML on each `JobObject` and transitions them to `Optimizing` so the orchestration service calls Claude with full execution plan context.

**Phase 2 — Poll for optimization results**
`PollForResultsWithTimeoutAsync` → `GET /api/agent/jobs/{jobId}/results` every **10 seconds**, **30-minute timeout**.
- Returns `null` when backend responds `204 NoContent` (not ready) or `200 OK` with empty list (treated the same).
- Returns the list once objects with optimized definitions are ready.

**Phase 3 — Benchmark optimized versions**
Wrapped in a `try/finally`. For each `JobObjectDto` in the results: `SubmitOptimizedMetricsAsync`. After all objects complete (success or failure), the `finally` block calls `DropStagingSchemaAsync` to clean up the entire staging schema.

### `SubmitOptimizedMetricsAsync`

Executes both original and optimized versions for every parameter set and posts all results together.

1. Read `stagingSchema = obj.StagingSchema`. If empty → log warning and skip (object came from a code path with no staging schema; should not happen).
2. **Per-object pre-deploy collision check:** `SqlObjectExecutor.StagingObjectExistsAsync(stagingSchema, objectName)`. If the object already exists under the staging schema → call `ReportExecutionFailedAsync` → return early.
3. Determine parameter sets to use — `obj.ParameterSets` if present, otherwise a synthetic empty set.
4. **Determinism probe:** `SqlObjectExecutor.ProbeIsDeterministicAsync(obj, firstParamSet.ParametersJson)` — runs the original object twice with identical parameters and compares result hashes. The `isDeterministic` result is passed to validation to skip checksums for non-deterministic objects.
5. Deploy optimized version once: `SqlObjectExecutor.DeployUnderOptimizerSchemaAsync(stagingSchema, ...)` (reused for all param sets). If deploy fails → calls `ReportExecutionFailedAsync` and returns early.
6. For each parameter set:
   - **Original execution:** `SqlObjectExecutor.ExecuteAndCaptureAsync(obj, parametersJson)` → `ExecutionResultDto` with `ExecutionVersionId = 1`.
   - If original fails → `continue` to next param set (optimized skipped for this set).
   - **Optimized execution:** `SqlObjectExecutor.ExecuteAndCaptureAsync(optimizerObj with SchemaName=stagingSchema, parametersJson)` → `ExecutionResultDto` with `ExecutionVersionId = 2`. Non-fatal — if optimized fails, original metrics are still posted.
   - **Sampled checksum:** If either side's result set exceeded `ChecksumRowThreshold`, calls `ComputeSampledChecksumAsync` for both sides and uses the sampled hashes for validation.
   - **Validation:** `ExecutionValidation.Compare` with row counts, column schemas, checksums, float samples, and `isDeterministic` flag.
7. Cleanup: `SqlObjectExecutor.RemoveFromOptimizerSchemaAsync(stagingSchema, ...)` in `finally`.
8. If `results.Count == 0` → call `ReportExecutionFailedAsync`.
9. `BackendApiClient.SubmitMetricsAsync` → `POST /api/agent/jobs/{jobId}/metrics`.
10. If submit fails → call `ReportExecutionFailedAsync`.

> **`SubmitObjectMetricsAsync`** (single-param-set version) still exists in `AgentWorker.cs` but is **dead code** — it is not called anywhere. The current path always goes through `SubmitOptimizedMetricsAsync`.

---

## Staging Schema

Each job gets a unique staging schema name (`dbopt_XXXXXX` where XXXXXX is 6 lowercase hex chars) provided by the backend on every `JobDto` and `JobObjectDto`. The agent **never generates or hardcodes** the schema name — it always comes from the backend.

**Lifecycle:**
1. Backend generates the name at job creation and checks for collision at discovery time.
2. Agent reads it from `obj.StagingSchema` on every `JobObjectDto`.
3. Before deploying, agent calls `StagingObjectExistsAsync` to check if the object already exists under that schema — calls `ReportExecutionFailedAsync(skipRefund: true)` if it does (though `skipRefund` is currently ignored by the backend since credits were removed).
4. After all objects in a job complete (in `finally`), agent calls `DropStagingSchemaAsync` to drop the entire schema.

**`SqlObjectExecutor` methods that take `stagingSchema`:**
- `DeployUnderOptimizerSchemaAsync(stagingSchema, ...)` — creates schema if absent, rewrites DDL header, executes.
- `RemoveFromOptimizerSchemaAsync(stagingSchema, ...)` — drops the individual object (called per-object in `finally`).
- `StagingObjectExistsAsync(stagingSchema, objectName, ct)` — queries `sys.objects JOIN sys.schemas`.
- `DropStagingSchemaAsync(stagingSchema, ct)` — executes `DROP SCHEMA IF EXISTS [escapedSchema]` (called job-level in `finally`).

Both `EscapeIdentifier` and `EscapeStringLiteral` helpers are used to safely build the SQL for these operations.

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
3. `GetObjectsWithPermanentWritesAsync` — queries `sys.dm_sql_referenced_entities` via `CROSS APPLY` to identify objects that write to permanent tables (`is_updated = 1`). Excludes temp tables (`#`-prefixed). Returns a `HashSet<string>` of `"schema.name"` keys. Silently swallows `SqlException` (e.g. broken dependencies) and defaults all objects to `HasPermanentTableWrites = false`. The backend uses this flag during classification to mark objects as `HasWrites`.
4. `GetParameterMetadataAsync` — queries `sys.parameters` for all crawled objects. Returns a dictionary mapping `"schema.name"` to `List<DiscoveredParameterDto>`. Constructs readable SQL type strings (e.g. `VARCHAR(MAX)`, `DECIMAL(18,4)`) with proper handling of nvarchar byte-length conversion.
5. Main query: `sys.objects JOIN sys.sql_modules JOIN sys.schemas` for types `P, V, FN, IF, TF`, excluding `is_ms_shipped = 1` objects.
6. For each object: maps `sys.objects.type` code to `ObjectTypeId` (inlined constants — Core not referenced), looks up frequency, runs `ExtractHints`, attaches parameter metadata and `HasPermanentTableWrites`, builds a `DiscoveredObjectDto`.

**`GetSchemaNames()`:**
Queries `SELECT name FROM sys.schemas` and returns all schema names. Called during `RunDiscoveryAsync` and passed to the backend as `ExistingSchemaNames` in `PostAgentDiscoveryRequest` so the backend can check the generated staging schema name for collisions. Swallows `SqlException` with a warning log and returns an empty list on failure.

**Object type codes → ObjectTypeId:**
| sys.objects.type | ObjectTypeId |
|-----------------|-------------|
| `P` | 1 (StoredProcedure) |
| `V` | 2 (View) |
| `FN` | 3 (ScalarFunction) |
| `IF` | 4 (TableValuedFunction) |
| `TF` | 5 (MultiStatementTVF) |

> These constants are inlined in the crawler rather than imported from `DbOptimizer.Core`, because the agent does not reference Core. They must stay in sync with `ObjectTypeIds` in the backend.

**Parameter metadata** is crawled via `GetParameterMetadataAsync` from `sys.parameters` for stored procedures and stored in `DiscoveredObjectDto.Parameters`. The backend's `ParameterOptionalityParser` determines which are optional from the SP definition. This JSON ends up in `JobObject.ParametersJson` and is used by the AI parameter generation feature.

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

### `DeployUnderOptimizerSchemaAsync(stagingSchema, ...)`

1. Ensures the staging schema exists (`IF NOT EXISTS ... EXEC('CREATE SCHEMA [escapedSchema]')`).
2. `RewriteObjectHeader(stagingSchema, ...)` — replaces the first `CREATE [OR ALTER] PROCEDURE/FUNCTION/VIEW [schema].[name]` with `CREATE OR ALTER {KEYWORD} [stagingSchema].[name]` using a compiled regex.
3. Executes the rewritten DDL.

### `RemoveFromOptimizerSchemaAsync(stagingSchema, ...)`

Runs `DROP {PROCEDURE|VIEW|FUNCTION} IF EXISTS [stagingSchema].[name]`. Always called in `finally` after optimized execution (per-object cleanup).

### `StagingObjectExistsAsync(stagingSchema, objectName, ct)`

Queries `sys.objects JOIN sys.schemas` with parameterized SQL to check if an object with the given name already exists under the staging schema. Called before deployment as a pre-flight collision check.

### `DropStagingSchemaAsync(stagingSchema, ct)`

Executes `DROP SCHEMA IF EXISTS [escapedSchema]`. Called in the job-level `finally` block after all objects in a job have been processed (regardless of success or failure). Cleans up the entire staging schema at once.

### `ProbeIsDeterministicAsync(jobObject, parametersJson, ct)`

Executes the original object twice with identical resolved parameter values and compares XxHash64 hashes of the result sets. Returns `true` if both hashes match (deterministic), `false` if they differ (non-deterministic, e.g. NEWID(), GETDATE()). Parameters with `$query` envelopes are resolved once and reused for both runs so a randomized sampling query does not create a false non-determinism signal. Each run uses its own connection to avoid temp-table conflicts. Called once per object before benchmarking — the result is passed to `ExecutionValidation.Compare` to skip checksum validation for non-deterministic objects.

### `ComputeSampledChecksumAsync(jobObject, parametersJson, totalRows, ct)`

Re-executes the object and computes an XxHash64 over a 3-band statistical sample (first 500 rows, middle 500 rows, last 500 rows). Used when `ExecuteAndCaptureAsync` reports `ChecksumThresholdExceeded` (result set larger than `ChecksumRowThreshold`, default 10,000 rows) so that large result sets still get a meaningful checksum comparison. The `totalRows` parameter (from the earlier execution) is used to position the middle and last bands.

---

## Result Set Validation

When both original and optimized versions execute successfully, the agent validates that the optimized version returns equivalent results. Validation runs inside `SubmitOptimizedMetricsAsync` and populates fields on `ExecutionResultDto`.

**Determinism probe:** Before benchmarking each object, `ProbeIsDeterministicAsync` runs the original object twice with identical parameters and compares result hashes. If non-deterministic (`isDeterministic = false`), checksum validation is skipped entirely to avoid false mismatches from objects using NEWID(), GETDATE(), etc.

**`ExecutionValidation.cs`** — `Compare` method:
- **Row count** (`RowCountMatch`) — original vs. optimized row counts must match exactly.
- **Column schema** (`ColumnSchemaMatch`) — column names and types must match (order-insensitive).
- **Checksum** (`ChecksumMatch`) — hash of result data. Skipped if `isDeterministic = false`.
- Skips validation entirely if either side has no result set.

**`ResultSetHasher.cs`** — computes checksums:
- `HashCurrentResultSetAsync`: Streams XxHash64 over the result set. Excludes imprecise types (float, real, money) from the hash. Stops hashing at `ChecksumRowThreshold` rows but continues counting; sets `ThresholdExceeded = true` if exceeded. Captures up to 500 float column values for approximate comparison.
- Rows ≤ `ChecksumRowThreshold` (default 10,000): full checksum over all rows.
- Rows > threshold: `ComputeSampledChecksumAsync` is called separately — re-executes the object and hashes a 3-band sample (first 500, middle 500, last 500 rows). `UsedSampledChecksum=true` is recorded.
- Float columns are compared with epsilon tolerance (`FloatEpsilon`, default 0.0001). When floats differ only within tolerance, `FloatColumnsApproximatelyEqual=true` is set even if the checksum technically mismatches.
- When checksum includes columns that were excluded due to imprecise types, `ChecksumExcludedImpreciseColumns=true` is recorded.

Validation failures do **not** abort metric submission — they are informational fields that the dashboard can surface to the user.

---

## `BackendApiClient`

Thin HTTP client. `X-Agent-ApiKey` is added to `DefaultRequestHeaders` at construction. All methods swallow non-cancellation exceptions and return `null`/`false` on failure — the agent's job loop treats these as transient failures and continues.

| Method | Endpoint | Notes |
|--------|----------|-------|
| `PollForJobAsync` | `POST /api/agent/poll` | Returns null on exception or `204 NoContent` |
| `PostDiscoveryAsync(jobId, objects, existingSchemaNames)` | `POST /api/agent/discovery` | Sends schema names for collision check |
| `StartJobAsync` | `POST /api/agent/jobs/{id}/start` | Returns false on exception |
| `GetJobObjectsAsync` | `GET /api/agent/jobs/{id}/objects` | Returns null on exception |
| `SubmitBaselinesAsync` | `POST /api/agent/jobs/{id}/baselines` | Returns false on exception |
| `PollForResultsAsync` | `GET /api/agent/jobs/{id}/results` | Returns null on exception, `204 NoContent`, or `200 []` |
| `SubmitMetricsAsync` | `POST /api/agent/jobs/{id}/metrics` | Returns false on exception |
| `ReportExecutionFailedAsync(jobId, objectId, reason, ct, skipRefund)` | `POST /api/agent/jobs/{id}/objects/{oid}/execution-failed` | Sends `{ reason, skipRefund }` — backend marks object Failed. `skipRefund` is sent but currently ignored by the backend (credit system removed). |
| `SendHeartbeatAsync` | `POST /api/agent/heartbeat` | Returns false on exception |

---

## Job Processing Flow (Agent's Perspective)

```
[idle]
  ↓ poll interval
POST /api/agent/poll  (body: AgentPollRequest with version + masked server metadata)
  ├─ MustUpdate=true       →  stop service
  ├─ RunDiscovery=true     →  GetSchemaNames → CrawlObjectsAsync → POST /api/agent/discovery → continue
  ├─ PendingJobs[0] = job J  →  ProcessJobAsync(J)
  │     ↓
  │   POST /api/agent/jobs/J/start
  │     ↓
  │   GET /api/agent/jobs/J/objects  (returns objects + parameter sets + StagingSchema)
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
  │   Phase 3: SubmitOptimizedMetricsAsync for each optimized object  [try]
  │     StagingObjectExistsAsync → if exists: ReportExecutionFailed, skip
  │     ProbeIsDeterministicAsync(original, firstParamSet) → isDeterministic
  │     DeployUnderOptimizerSchemaAsync(stagingSchema)
  │       (if deploy fails: ReportExecutionFailed, return early)
  │     for each ParameterSet:
  │       ExecuteAndCaptureAsync(original)  → ExecutionVersionId=1
  │       ExecuteAndCaptureAsync([stagingSchema].[name])  → ExecutionVersionId=2
  │       (if threshold exceeded: ComputeSampledChecksumAsync for both sides)
  │       ExecutionValidation.Compare(isDeterministic, ...)
  │     RemoveFromOptimizerSchemaAsync(stagingSchema)  [finally, per-object]
  │     POST /api/agent/jobs/J/metrics
  │     (if all param sets failed: POST /api/agent/jobs/J/objects/{id}/execution-failed)
  │   DropStagingSchemaAsync(stagingSchema)  [finally, job-level]
  │     ↓
  │   [job complete for this agent]
  │
  ├─ ReadyToExecuteObjects.Count > 0  →  group by JobId
  │   for each job group:  [try]
  │     SubmitOptimizedMetricsAsync for each object
  │   DropStagingSchemaAsync(stagingSchema)  [finally, job-level]
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

## Authentication Error Handling (401/403)

`BackendApiClient.PollForJobAsync` checks for 401/403 responses before `EnsureSuccessStatusCode()`:
- **401 Unauthorized** → throws `AgentAuthException("InvalidApiKey")`
- **403 Forbidden** → deserializes the response body, extracts the `error` property, throws `AgentAuthException(errorCode)`. Expected error codes: `AgentDisabled`, `OrgSuspended`.

`AgentAuthException` is **re-thrown** through the generic catch clause (other exceptions return `null`).

`AgentWorker.ExecuteAsync` wraps the entire poll loop in `try/catch (AgentAuthException)`:
- `InvalidApiKey` → logs Error ("key may have been rotated")
- `AgentDisabled` → logs Warning ("disabled from dashboard")
- `OrgSuspended` → logs Warning ("organization suspended")
- All cases → calls `_lifetime.StopApplication()` for graceful shutdown

The agent injects `IHostApplicationLifetime` to support this graceful stop.

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
- **Staging schema** (`dbopt_XXXXXX`) is created in the customer database per job, used as a sandbox for running the optimized version, and always dropped in a job-level `finally`. The name comes from the backend — never hardcoded or generated by the agent.
- **SQL injection safety:** `EscapeIdentifier(value)` wraps in `[` `]` with `]` doubled. `EscapeStringLiteral(value)` wraps in `N'` `'` with `'` doubled. Both are used when building dynamic DDL for the staging schema.
- **Server/database names** are masked before being sent to the backend: first char + last 3 chars, rest replaced with `*`. A SHA-256 hash of `serverName|databaseName` (lowercased) is also sent for identity matching without revealing the full names.
- **Deploy failures are reported, not skipped.** If `DeployUnderOptimizerSchemaAsync` throws in `SubmitOptimizedMetricsAsync`, the method calls `ReportExecutionFailedAsync` and returns early — it does not attempt to benchmark with partial results.
- **`SubmitObjectMetricsAsync`** in `AgentWorker.cs` is dead code — it is never called. The active path is `SubmitOptimizedMetricsAsync`.
- **Result set validation** (`ResultSetHasher`, `ExecutionValidation`) runs after both original and optimized execute. Failures are non-fatal — they populate informational DTO fields only.
- **Determinism probe** runs before benchmarking each object — executes twice with identical resolved parameters and compares XxHash64. If non-deterministic, checksum validation is skipped to avoid false failures from NEWID(), GETDATE(), etc. Parameters are resolved once and reused for both probe runs.
- **Permanent table writes detection** via `GetObjectsWithPermanentWritesAsync` uses `sys.dm_sql_referenced_entities` with `is_updated = 1`. Sets `HasPermanentTableWrites` on `DiscoveredObjectDto` so the backend classifier can identify write-heavy objects via DMV data rather than pure static analysis. Silently defaults to `false` if the DMV call fails (e.g. broken dependencies).
- **Sampled checksum** for large result sets: when row count exceeds `ChecksumRowThreshold`, `ComputeSampledChecksumAsync` re-executes and hashes 3 bands of 500 rows (first, middle, last). Both original and optimized are sampled for comparison.
- **NuGet package** consumed is `SqlBrain.Contracts` (the published name), not `DbOptimizer.Contracts` (the project name in the backend repo). Bump the version in `DbOptimizer.Agent.csproj` whenever any Contracts file changes.
