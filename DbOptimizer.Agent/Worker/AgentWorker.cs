using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DbOptimizer.Agent.Configuration;
using DbOptimizer.Agent.Crawling;
using DbOptimizer.Agent.Http;
using DbOptimizer.Contracts.Dtos;
using DbOptimizer.Contracts.Requests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbOptimizer.Agent.Worker;

public class AgentWorker : BackgroundService
{
    private readonly BackendApiClient _api;
    private readonly SqlServerCrawler _crawler;
    private readonly SqlObjectExecutor _executor;
    private readonly AgentConfiguration _config;
    private readonly ILogger<AgentWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private static readonly TimeSpan ResultPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResultPollTimeout  = TimeSpan.FromMinutes(30);

    private DateTime _lastHeartbeat = DateTime.MinValue;

    // Cached on first successful crawl; connected server/database don't change at runtime.
    private string? _reportedServerName;
    private string? _reportedDatabaseName;
    private string? _reportedSqlServerVersion;
    private int?    _reportedCompatibilityLevel;

    public AgentWorker(
        BackendApiClient api,
        SqlServerCrawler crawler,
        SqlObjectExecutor executor,
        IOptions<AgentConfiguration> config,
        ILogger<AgentWorker> logger,
        IHostApplicationLifetime lifetime)
    {
        _api      = api;
        _crawler  = crawler;
        _executor = executor;
        _config   = config.Value;
        _logger   = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentWorker started. Polling {BackendUrl} every {PollInterval}s",
            _config.BackendUrl, _config.PollIntervalSeconds);

        try
        {

        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySendHeartbeatAsync(stoppingToken);

            var pollRequest = new AgentPollRequest
            {
                AgentVersion       = GetAgentVersion(),
                ServerNameMasked   = _reportedServerName   is not null ? MaskName(_reportedServerName)   : null,
                DatabaseNameMasked = _reportedDatabaseName is not null ? MaskName(_reportedDatabaseName) : null,
                ServerDatabaseHash = _reportedServerName   is not null && _reportedDatabaseName is not null
                                         ? ComputeServerDatabaseHash(_reportedServerName, _reportedDatabaseName)
                                         : null,
                SqlServerVersion   = _reportedSqlServerVersion,
                CompatibilityLevel = _reportedCompatibilityLevel,
            };

            var poll = await _api.PollForJobAsync(pollRequest, stoppingToken);

            if (poll is null)
            {
                await DelayAsync(_config.PollIntervalSeconds, stoppingToken);
                continue;
            }

            if (poll.MustUpdate)
            {
                _logger.LogCritical(
                    "Backend requires a mandatory agent update. Stopping poll loop. " +
                    "Please update the agent to the latest version before restarting.");
                return;
            }

            if (poll.UpdateAvailable)
            {
                _logger.LogWarning(
                    "A newer agent version is available. Consider updating soon " +
                    "to avoid being blocked by a future mandatory update.");
            }

            if (poll.DiscoveryJobId.HasValue)
            {
                await RunDiscoveryAsync(poll.DiscoveryJobId.Value, stoppingToken);
                continue;
            }

            if (poll.PendingJobs.Count > 0)
            {
                var job = poll.PendingJobs[0];
                await ProcessJobAsync(job, stoppingToken);
            }
            else if (poll.ReadyToExecuteObjects.Count > 0)
            {
                _logger.LogInformation("Submitting metrics for {Count} ready object(s) from running job(s)",
                    poll.ReadyToExecuteObjects.Count);

                // Group by job so we can drop each job's staging schema after all its objects complete.
                var objectsByJob = poll.ReadyToExecuteObjects
                    .GroupBy(o => o.JobId)
                    .ToList();

                foreach (var jobGroup in objectsByJob)
                {
                    var stagingSchema = jobGroup.First().StagingSchema;
                    try
                    {
                        foreach (var obj in jobGroup)
                            await SubmitOptimizedMetricsAsync(obj.JobId, obj, stoppingToken);
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(stagingSchema))
                        {
                            try { await _executor.DropStagingSchemaAsync(stagingSchema, stoppingToken); }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "Job {JobId}: failed to drop staging schema [{StagingSchema}] — may require manual cleanup",
                                    jobGroup.Key, stagingSchema);
                            }
                        }
                    }
                }
            }
            else
            {
                await DelayAsync(_config.PollIntervalSeconds, stoppingToken);
            }
        }

        } // end try
        catch (AgentAuthException ex) when (!stoppingToken.IsCancellationRequested)
        {
            switch (ex.ErrorCode)
            {
                case "InvalidApiKey":
                    _logger.LogError(
                        "API key rejected — key may have been rotated. Reconfigure the agent with the new key.");
                    break;
                case "AgentDisabled":
                    _logger.LogWarning(
                        "This agent has been disabled from the dashboard. Contact your administrator.");
                    break;
                case "OrgSuspended":
                    _logger.LogWarning(
                        "Your organization account is suspended. Contact DbOptimizer support.");
                    break;
                default:
                    _logger.LogError(
                        "Authentication rejected by backend (error: {ErrorCode}). Stopping agent.", ex.ErrorCode);
                    break;
            }
            _lifetime.StopApplication();
        }

        _logger.LogInformation("AgentWorker stopping.");
    }

    // -------------------------------------------------------------------------
    // Discovery
    // -------------------------------------------------------------------------

    private async Task RunDiscoveryAsync(int jobId, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running discovery for job {JobId}: crawling SQL Server objects", jobId);

        try
        {
            var definitions  = await _crawler.CrawlObjectsAsync(stoppingToken);
            var schemaNames  = await _crawler.GetSchemaNames(stoppingToken);
            _logger.LogInformation("Discovery: crawled {Count} objects, {SchemaCount} schemas",
                definitions.Count, schemaNames.Count);

            var sessionId = await _api.PostDiscoveryAsync(jobId, definitions, schemaNames, stoppingToken);
            if (sessionId is null)
            {
                _logger.LogError("Discovery: failed to post objects to backend — will retry next cycle");
                return;
            }

            _logger.LogInformation("Discovery: session {SessionId} created with {Count} objects. " +
                "Waiting for user to select objects in dashboard.", sessionId, definitions.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Discovery cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery: unhandled exception — returning to polling");
        }
    }

    // -------------------------------------------------------------------------
    // Job processing
    // -------------------------------------------------------------------------

    private async Task ProcessJobAsync(JobDto job, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Processing job {JobId}", job.Id);

        try
        {
            var started = await _api.StartJobAsync(job.Id, stoppingToken);
            if (!started)
            {
                _logger.LogError("Job {JobId}: failed to start job — aborting", job.Id);
                return;
            }

            // Phase 1: Fetch job objects with parameter sets and run baseline executions.
            // This captures actual execution plans so Claude can optimize with full context.
            var jobObjects = await _api.GetJobObjectsAsync(job.Id, stoppingToken);
            if (jobObjects is null)
            {
                _logger.LogError("Job {JobId}: failed to fetch job objects — aborting", job.Id);
                return;
            }

            _logger.LogInformation("Job {JobId}: running baseline executions for {Count} objects",
                job.Id, jobObjects.Count);

            await RunBaselineExecutionsAsync(job.Id, jobObjects, stoppingToken);

            // Phase 2: Poll for Claude's optimized results.
            _logger.LogInformation("Job {JobId}: baselines complete, waiting for optimization results", job.Id);

            var results = await PollForResultsWithTimeoutAsync(job.Id, stoppingToken);
            if (results is null)
            {
                _logger.LogError(
                    "Job {JobId}: timed out after {Timeout} waiting for optimization results",
                    job.Id, ResultPollTimeout);
                return;
            }

            _logger.LogInformation("Job {JobId}: received {Count} optimized objects — submitting benchmarks",
                job.Id, results.Count);

            // Phase 3: Execute optimized versions only (originals already captured in baseline).
            // The finally block drops the entire staging schema as a catch-all in case any
            // per-object cleanup was missed during benchmarking.
            try
            {
                foreach (var obj in results)
                    await SubmitOptimizedMetricsAsync(job.Id, obj, stoppingToken);
            }
            finally
            {
                if (!string.IsNullOrEmpty(job.StagingSchema))
                {
                    try { await _executor.DropStagingSchemaAsync(job.StagingSchema, stoppingToken); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Job {JobId}: failed to drop staging schema [{StagingSchema}] — may require manual cleanup",
                            job.Id, job.StagingSchema);
                    }
                }
            }

            _logger.LogInformation("Job {JobId}: completed", job.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job {JobId}: cancelled", job.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: unhandled exception — returning to polling", job.Id);
        }
    }

    /// <summary>
    /// Fetches the estimated execution plan for each job object using SET SHOWPLAN_XML ON.
    /// No parameters are required — SQL Server compiles and returns the plan without executing.
    /// Plans are posted to the baselines endpoint; the backend stores each plan on the JobObject
    /// and transitions it to Optimizing so the orchestration service can call Claude with full
    /// execution plan context.
    /// Execution metrics are NOT captured here — both original and optimized are benchmarked
    /// together in Phase 3 for a fair side-by-side comparison.
    /// Objects that fail plan capture are reported via execution-failed.
    /// </summary>
    private async Task RunBaselineExecutionsAsync(
        int jobId,
        List<JobObjectDto> jobObjects,
        CancellationToken stoppingToken)
    {
        var baselineResults = new List<BaselineObjectResult>();

        foreach (var obj in jobObjects)
        {
            // Use SHOWPLAN_XML to get the estimated execution plan without executing the object.
            // This avoids requiring parameter values at this phase — parameters are applied in
            // Phase 3 when both original and optimized are benchmarked side by side.
            try
            {
                var planXml = await _executor.GetEstimatedPlanAsync(obj, stoppingToken);

                baselineResults.Add(new BaselineObjectResult
                {
                    JobObjectId      = obj.Id,
                    ExecutionPlanXml = planXml,
                    Metrics          = [] // Metrics are captured in Phase 3 for a fair comparison
                });

                _logger.LogInformation(
                    "Job {JobId}: estimated plan captured for [{Schema}].[{Object}]",
                    jobId, obj.SchemaName, obj.ObjectName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Job {JobId}: baseline plan capture failed for [{Schema}].[{Object}] — reporting failure",
                    jobId, obj.SchemaName, obj.ObjectName);

                await _api.ReportExecutionFailedAsync(
                    jobId, obj.Id,
                    $"Baseline plan capture failed: {ex.Message}",
                    stoppingToken);
            }
        }

        if (baselineResults.Count > 0)
        {
            var ok = await _api.SubmitBaselinesAsync(
                jobId,
                new PostBaselineResultsRequest { Results = baselineResults },
                stoppingToken);

            if (!ok)
                _logger.LogError("Job {JobId}: failed to submit baseline results to backend", jobId);
        }
    }

    /// <summary>
    /// Polls for results at 10-second intervals until they arrive or the 30-minute timeout elapses.
    /// Continues polling on both 204 NoContent and 200 OK with empty list.
    /// </summary>
    private async Task<List<JobObjectDto>?> PollForResultsWithTimeoutAsync(int jobId, CancellationToken stoppingToken)
    {
        var deadline = DateTime.UtcNow + ResultPollTimeout;

        while (DateTime.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            var results = await _api.PollForResultsAsync(jobId, stoppingToken);

            // PollForResultsAsync returns null for 204 AND for 200+empty (both mean "not ready").
            if (results is not null)
                return results;

            _logger.LogDebug("Job {JobId}: results not ready, retrying in {Interval}s",
                jobId, ResultPollInterval.TotalSeconds);
            await Task.Delay(ResultPollInterval, stoppingToken);

            await TrySendHeartbeatAsync(stoppingToken);
        }

        return null;
    }

    /// <summary>
    /// Executes both the original and optimized versions of a job object at the same point in time
    /// and posts metrics. Running them together ensures a fair side-by-side comparison with the
    /// same parameter set, same DB state, and the same cache conditions.
    /// </summary>
    private async Task SubmitOptimizedMetricsAsync(int jobId, JobObjectDto obj, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Job {JobId}: benchmarking optimized [{Schema}].[{Object}]",
            jobId, obj.SchemaName, obj.ObjectName);

        var stagingSchema    = obj.StagingSchema;
        var hasRealParamSets = obj.ParameterSets?.Count > 0;
        var paramSetsToRun   = hasRealParamSets
            ? obj.ParameterSets!
            : [new ParameterSetDto { Id = 0, JobObjectId = obj.Id, Label = "Default", ParametersJson = string.Empty, IsDefault = true }];

        var results = new List<ExecutionResultDto>();

        // Deploy the optimized version once — reused across all parameter set runs.
        var optimizedDeployed = false;
        if (!string.IsNullOrWhiteSpace(obj.OptimizedDefinition))
        {
            if (string.IsNullOrEmpty(stagingSchema))
            {
                _logger.LogWarning(
                    "Job {JobId}: [{Schema}].[{Object}] has no staging schema — optimized benchmark skipped",
                    jobId, obj.SchemaName, obj.ObjectName);
            }
            else
            {
                // Per-object collision check: if [stagingSchema].[ObjectName] already exists on the
                // customer database, the schema was somehow not clean at discovery time. Mark the
                // object Failed without a credit refund (Claude already ran) and skip execution.
                var collisionDetected = false;
                try
                {
                    collisionDetected = await _executor.StagingObjectExistsAsync(stagingSchema, obj.ObjectName, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Job {JobId}: staging collision check failed for [{Schema}].[{Object}] — proceeding with deployment",
                        jobId, stagingSchema, obj.ObjectName);
                }

                if (collisionDetected)
                {
                    _logger.LogError(
                        "Job {JobId}: [{StagingSchema}].[{Object}] already exists — " +
                        "staging object collision detected at deploy time (no credit refund)",
                        jobId, stagingSchema, obj.ObjectName);
                    await _api.ReportExecutionFailedAsync(jobId, obj.Id,
                        "Staging object collision detected — schema was validated clean at discovery time",
                        stoppingToken,
                        skipRefund: true);
                    return;
                }

                try
                {
                    await _executor.DeployUnderOptimizerSchemaAsync(obj, obj.OptimizedDefinition, stagingSchema, stoppingToken);
                    optimizedDeployed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Job {JobId}: failed to deploy [{StagingSchema}].[{Object}] — optimized benchmarks will be skipped",
                        jobId, stagingSchema, obj.ObjectName);
                }
            }
        }

        // --- Determinism probe (runs once per object, before benchmarking) ---
        // Uses the first parameter set with pre-resolved values so a $query like
        // "SELECT TOP 1 Id ORDER BY NEWID()" does not produce a false non-determinism signal.
        bool? probeIsDeterministic = null;
        try
        {
            var probeParamSet = paramSetsToRun[0];
            probeIsDeterministic = await _executor.ProbeIsDeterministicAsync(
                obj, probeParamSet.ParametersJson ?? string.Empty, stoppingToken);

            if (probeIsDeterministic == false)
                _logger.LogWarning(
                    "Job {JobId}: [{Schema}].[{Object}] produced different checksums on two consecutive " +
                    "executions — object is non-deterministic, checksum comparison will be skipped",
                    jobId, obj.SchemaName, obj.ObjectName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Job {JobId}: determinism probe failed for [{Schema}].[{Object}] — assuming deterministic",
                jobId, obj.SchemaName, obj.ObjectName);
            // probeIsDeterministic stays null; benchmarking proceeds with checksum comparison enabled.
        }

        try
        {
            foreach (var paramSet in paramSetsToRun)
            {
                var parametersJson = paramSet.ParametersJson ?? string.Empty;
                var parameterSetId = hasRealParamSets ? paramSet.Id : (int?)null;

                // --- Original execution ---
                CapturedMetrics original;
                try
                {
                    original = await _executor.ExecuteAndCaptureAsync(obj, parametersJson, stoppingToken);

                    results.Add(new ExecutionResultDto
                    {
                        JobObjectId             = obj.Id,
                        ParameterSetId          = parameterSetId,
                        ExecutionVersionId      = 1,
                        ExecutionMs             = original.ExecutionMs,
                        LogicalReads            = original.LogicalReads,
                        CpuTimeMs               = original.CpuTimeMs,
                        RowsReturned            = original.RowsReturned,
                        ExecutionPlanXml        = original.ExecutionPlanXml,
                        MissingIndexSuggestions = string.Join('\n', original.MissingIndexSuggestions),
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Job {JobId}: original execution failed for [{Schema}].[{Object}] param set '{Label}' — skipping this set",
                        jobId, obj.SchemaName, obj.ObjectName, paramSet.Label);
                    continue; // Skip optimized run for this param set too; move to next
                }

                // --- Optimized execution ---
                if (optimizedDeployed)
                {
                    try
                    {
                        var optimizerObj = new JobObjectDto
                        {
                            Id           = obj.Id,
                            SchemaName   = stagingSchema,
                            ObjectName   = obj.ObjectName,
                            ObjectTypeId = obj.ObjectTypeId,
                        };

                        var optimized = await _executor.ExecuteAndCaptureAsync(optimizerObj, parametersJson, stoppingToken);

                        // --- Phase 3: sampled checksum for large result sets ---
                        // If either side exceeded the row threshold, re-execute both under
                        // 3-band sampling so large result sets still get a checksum comparison.
                        var originalForCompare = original;
                        var optimizedForCompare = optimized;

                        if (original.ChecksumThresholdExceeded && original.ColumnSchema is not null)
                        {
                            try
                            {
                                var sampledOrig = await _executor.ComputeSampledChecksumAsync(
                                    obj, parametersJson, original.RowsReturned, stoppingToken);
                                var sampledOpt  = await _executor.ComputeSampledChecksumAsync(
                                    optimizerObj, parametersJson, optimized.RowsReturned, stoppingToken);

                                originalForCompare  = original  with { ResultHash = sampledOrig };
                                optimizedForCompare = optimized with { ResultHash = sampledOpt };

                                _logger.LogDebug(
                                    "Job {JobId}: sampled checksums computed for [{Schema}].[{Object}]",
                                    jobId, obj.SchemaName, obj.ObjectName);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "Job {JobId}: sampled checksum failed for [{Schema}].[{Object}] — skipping checksum comparison",
                                    jobId, obj.SchemaName, obj.ObjectName);
                                // ResultHash stays null; Compare will leave ChecksumMatch null.
                            }
                        }

                        // probeIsDeterministic ?? true: if the probe failed (null), assume deterministic
                        // and run checksum; the IsDeterministic field on the DTO stays null to signal
                        // the probe didn't complete.
                        var validation = ExecutionValidation.Compare(
                            originalForCompare, optimizedForCompare,
                            isDeterministic: probeIsDeterministic ?? true,
                            floatEpsilon: _config.FloatEpsilon);

                        results.Add(new ExecutionResultDto
                        {
                            JobObjectId                      = obj.Id,
                            ParameterSetId                   = parameterSetId,
                            ExecutionVersionId               = 2,
                            ExecutionMs                      = optimized.ExecutionMs,
                            LogicalReads                     = optimized.LogicalReads,
                            CpuTimeMs                        = optimized.CpuTimeMs,
                            RowsReturned                     = optimized.RowsReturned,
                            ExecutionPlanXml                 = optimized.ExecutionPlanXml,
                            MissingIndexSuggestions          = string.Join('\n', optimized.MissingIndexSuggestions),
                            RowCountMatch                    = validation.RowCountMatch,
                            ColumnSchemaMatch                = validation.ColumnSchemaMatch,
                            ValidationSkipped                = validation.ValidationSkipped,
                            ValidationSkipReason             = validation.SkipReason,
                            IsDeterministic                  = probeIsDeterministic,
                            ChecksumMatch                    = validation.ChecksumMatch,
                            ChecksumExcludedImpreciseColumns = validation.ChecksumExcludedImpreciseColumns,
                            UsedSampledChecksum              = validation.UsedSampledChecksum,
                            FloatColumnsApproximatelyEqual   = validation.FloatColumnsApproximatelyEqual,
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Job {JobId}: optimized execution failed for [{Schema}].[{Object}] param set '{Label}'",
                            jobId, obj.SchemaName, obj.ObjectName, paramSet.Label);
                        // Non-fatal — original metrics for this param set are still posted.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Job {JobId}: unexpected error benchmarking [{Schema}].[{Object}]",
                jobId, obj.SchemaName, obj.ObjectName);
        }
        finally
        {
            if (optimizedDeployed)
            {
                try { await _executor.RemoveFromOptimizerSchemaAsync(obj, stagingSchema, stoppingToken); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Job {JobId}: cleanup of [{StagingSchema}].[{Object}] failed",
                        jobId, stagingSchema, obj.ObjectName);
                }
            }
        }

        if (results.Count == 0)
        {
            _logger.LogError(
                "Job {JobId}: no benchmark results captured for [{Schema}].[{Object}] — reporting failure",
                jobId, obj.SchemaName, obj.ObjectName);
            await _api.ReportExecutionFailedAsync(jobId, obj.Id,
                "All parameter set executions failed", stoppingToken);
            return;
        }

        var request = new PostExecutionResultsRequest { JobObjectId = obj.Id, Results = results };
        var ok = await _api.SubmitMetricsAsync(jobId, request, stoppingToken);
        if (!ok)
        {
            _logger.LogError(
                "Job {JobId}: failed to submit benchmark metrics for [{Schema}].[{Object}]",
                jobId, obj.SchemaName, obj.ObjectName);
            await _api.ReportExecutionFailedAsync(jobId, obj.Id,
                "Failed to submit benchmark metrics to backend", stoppingToken);
        }
    }

    /// <summary>
    /// Executes both the original and optimized versions of a job object and posts metrics.
    /// For stored procedures without parameter sets, calls the execution-failed endpoint
    /// if the definition contains parameters.
    /// Wraps the entire execution block in a try/catch: any unhandled exception is reported
    /// via the execution-failed endpoint (with a credit refund) and execution continues
    /// to the next object.
    /// </summary>
    private async Task SubmitObjectMetricsAsync(int jobId, JobObjectDto obj, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Job {JobId}: executing [{Schema}].[{Object}] (TypeId={TypeId})",
            jobId, obj.SchemaName, obj.ObjectName, obj.ObjectTypeId);

        var defaultParamSet = obj.ParameterSets?.FirstOrDefault(p => p.IsDefault)
                              ?? obj.ParameterSets?.FirstOrDefault();
        var parametersJson = defaultParamSet?.ParametersJson ?? string.Empty;
        var parameterSetId = defaultParamSet?.Id; // null when no parameter set (views, parameter-less functions)

        var results = new List<ExecutionResultDto>();

        try
        {
            // --- Original execution ---
            CapturedMetrics original;
            try
            {
                original = await _executor.ExecuteAndCaptureAsync(obj, parametersJson, stoppingToken);

                results.Add(new ExecutionResultDto
                {
                    JobObjectId             = obj.Id,
                    ParameterSetId          = parameterSetId,
                    ExecutionVersionId      = 1,
                    ExecutionMs             = original.ExecutionMs,
                    LogicalReads            = original.LogicalReads,
                    CpuTimeMs               = original.CpuTimeMs,
                    RowsReturned            = original.RowsReturned,
                    ExecutionPlanXml        = original.ExecutionPlanXml,
                    MissingIndexSuggestions = string.Join('\n', original.MissingIndexSuggestions),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Job {JobId}: original execution failed for [{Schema}].[{Object}] — reporting failure",
                    jobId, obj.SchemaName, obj.ObjectName);

                await _api.ReportExecutionFailedAsync(
                    jobId, obj.Id,
                    $"Original execution failed: {ex.Message}",
                    stoppingToken);
                return;
            }

            // --- Optimized execution ---
            if (!string.IsNullOrWhiteSpace(obj.OptimizedDefinition))
            {
                var deployed = false;
                try
                {
                    await _executor.DeployUnderOptimizerSchemaAsync(obj, obj.OptimizedDefinition, obj.StagingSchema, stoppingToken);
                    deployed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Job {JobId}: failed to deploy [optimizer].[{Object}] — skipping optimized benchmark",
                        jobId, obj.ObjectName);
                }

                if (deployed)
                {
                    try
                    {
                        var optimizerObj = new JobObjectDto
                        {
                            Id           = obj.Id,
                            SchemaName   = "optimizer",
                            ObjectName   = obj.ObjectName,
                            ObjectTypeId = obj.ObjectTypeId,
                        };

                        var optimized = await _executor.ExecuteAndCaptureAsync(optimizerObj, parametersJson, stoppingToken);

                        results.Add(new ExecutionResultDto
                        {
                            JobObjectId             = obj.Id,
                            ParameterSetId          = parameterSetId,
                            ExecutionVersionId      = 2,
                            ExecutionMs             = optimized.ExecutionMs,
                            LogicalReads            = optimized.LogicalReads,
                            CpuTimeMs               = optimized.CpuTimeMs,
                            RowsReturned            = optimized.RowsReturned,
                            ExecutionPlanXml        = optimized.ExecutionPlanXml,
                            MissingIndexSuggestions = string.Join('\n', optimized.MissingIndexSuggestions),
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Job {JobId}: optimized execution failed for [{Schema}].[{Object}]",
                            jobId, obj.SchemaName, obj.ObjectName);
                        // Optimized execution failure is non-fatal: we still post the original metrics.
                    }
                    finally
                    {
                        // Optimizer schema cleanup always runs, even if optimized execution failed.
                        try
                        {
                            await _executor.RemoveFromOptimizerSchemaAsync(obj, obj.StagingSchema, stoppingToken);
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.LogWarning(cleanupEx,
                                "Job {JobId}: cleanup of [{StagingSchema}].[{Object}] failed — may require manual removal",
                                jobId, obj.StagingSchema, obj.ObjectName);
                        }
                    }
                }
            }

            var request = new PostExecutionResultsRequest
            {
                JobObjectId = obj.Id,
                Results     = results,
            };

            var ok = await _api.SubmitMetricsAsync(jobId, request, stoppingToken);
            if (!ok)
            {
                _logger.LogError(
                    "Job {JobId}: failed to submit metrics for [{Schema}].[{Object}] — marking object failed",
                    jobId, obj.SchemaName, obj.ObjectName);

                await _api.ReportExecutionFailedAsync(
                    jobId, obj.Id,
                    "Failed to submit execution metrics to backend",
                    stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch-all for any unexpected exception in the entire execution block.
            _logger.LogError(ex,
                "Job {JobId}: unexpected execution error for [{Schema}].[{Object}]",
                jobId, obj.SchemaName, obj.ObjectName);

            await _api.ReportExecutionFailedAsync(
                jobId, obj.Id,
                $"Unexpected execution error: {ex.Message}",
                stoppingToken);
        }
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    private async Task TrySendHeartbeatAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds);
        if (DateTime.UtcNow - _lastHeartbeat < interval)
            return;

        if (_reportedServerName is null)
        {
            try
            {
                var serverInfo = await _crawler.GetServerInfoAsync(stoppingToken);
                _reportedServerName         = serverInfo.ServerName;
                _reportedDatabaseName       = serverInfo.DatabaseName;
                _reportedSqlServerVersion   = serverInfo.SqlServerVersion;
                _reportedCompatibilityLevel = serverInfo.CompatibilityLevel;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve SQL Server info for heartbeat — will retry next cycle");
            }
        }

        var heartbeat = new HeartbeatRequest
        {
            AgentVersion             = GetAgentVersion(),
            MachineName              = MaskName(Environment.MachineName),
            ServerNameMasked         = _reportedServerName   is not null ? MaskName(_reportedServerName)   : null,
            DatabaseNameMasked       = _reportedDatabaseName is not null ? MaskName(_reportedDatabaseName) : null,
            ServerDatabaseHash       = _reportedServerName   is not null && _reportedDatabaseName is not null
                                           ? ComputeServerDatabaseHash(_reportedServerName, _reportedDatabaseName)
                                           : null,
            PollIntervalSeconds      = _config.PollIntervalSeconds,
            HeartbeatIntervalSeconds = _config.HeartbeatIntervalSeconds,
            HttpTimeoutSeconds       = _config.HttpTimeoutSeconds,
            SqlServerVersion         = _reportedSqlServerVersion,
            CompatibilityLevel       = _reportedCompatibilityLevel,
        };

        var ok = await _api.SendHeartbeatAsync(heartbeat, stoppingToken);
        if (ok)
            _lastHeartbeat = DateTime.UtcNow;
        else
            _logger.LogWarning("Heartbeat failed — will retry next cycle");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string GetAgentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static Task DelayAsync(int seconds, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(seconds), ct);

    /// <summary>
    /// Masks a name keeping first char + last N chars, rest replaced with '*':
    ///   ≥ 5 chars : first + last 3  e.g. "SQLSERVER01" → "S*******r01"
    ///   4 chars   : first + last 1  e.g. "PROD"        → "P**D"
    ///   3 chars   : first + last 1  e.g. "SRV"         → "S*V"
    ///   2 chars   : first only      e.g. "DB"          → "D*"
    ///   1 char    : fully masked    e.g. "A"           → "*"
    /// </summary>
    internal static string MaskName(string value) => value.Length switch
    {
        0 => value,
        1 => "*",
        2 => value[0] + "*",
        3 => value[0] + "*" + value[^1],
        4 => value[0] + "**" + value[^1],
        _ => value[0] + new string('*', value.Length - 4) + value[^3..]
    };

    /// <summary>
    /// SHA-256 hex of "serverName|databaseName" (lowercased).
    /// Computed from original unmasked values. The unique index (AgentId, Hash) scopes it per agent.
    /// </summary>
    internal static string ComputeServerDatabaseHash(string serverName, string databaseName)
    {
        var input = $"{serverName.ToLowerInvariant()}|{databaseName.ToLowerInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
