using System.Reflection;
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

    private static readonly TimeSpan ResultPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResultPollTimeout  = TimeSpan.FromMinutes(30);

    private DateTime _lastHeartbeat = DateTime.MinValue;

    // Cached on first successful query; the connected server/database don't change at runtime.
    private string? _reportedServerName;
    private string? _reportedDatabaseName;

    public AgentWorker(
        BackendApiClient api,
        SqlServerCrawler crawler,
        SqlObjectExecutor executor,
        IOptions<AgentConfiguration> config,
        ILogger<AgentWorker> logger)
    {
        _api      = api;
        _crawler  = crawler;
        _executor = executor;
        _config   = config.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentWorker started. Polling {BackendUrl} every {PollInterval}s",
            _config.BackendUrl, _config.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySendHeartbeatAsync(stoppingToken);

            var poll = await _api.PollForJobAsync(stoppingToken);

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

            // Discovery takes priority over job processing when the backend signals it.
            if (poll.RunDiscovery)
            {
                await RunDiscoveryAsync(stoppingToken);
                // After discovery, loop immediately so the next poll picks up any new work.
                continue;
            }

            if (poll.PendingJobs.Count > 0)
            {
                // Process one job per cycle — keeps each iteration bounded.
                var job = poll.PendingJobs[0];
                await ProcessJobAsync(job, stoppingToken);
            }
            else if (poll.ReadyToExecuteObjects.Count > 0)
            {
                // Objects from an already-running job are ready for benchmarking.
                // This handles the case where the agent restarted mid-job — the job is
                // no longer Pending so ProcessJobAsync won't be called, but Claude has
                // finished and the objects are AwaitingApproval in the poll response.
                _logger.LogInformation("Submitting metrics for {Count} ready object(s) from running job(s)",
                    poll.ReadyToExecuteObjects.Count);

                foreach (var obj in poll.ReadyToExecuteObjects)
                {
                    await SubmitObjectMetricsAsync(obj.JobId, obj, stoppingToken);
                }
                // Loop immediately — don't sleep, there may be more work.
            }
            else
            {
                await DelayAsync(_config.PollIntervalSeconds, stoppingToken);
            }
        }

        _logger.LogInformation("AgentWorker stopping.");
    }

    // -------------------------------------------------------------------------
    // Discovery
    // -------------------------------------------------------------------------

    /// <summary>
    /// Crawls the SQL Server and posts all discovered objects to the backend.
    /// The backend creates a DiscoverySession and notifies the dashboard via SignalR
    /// so the user can select which objects to optimize.
    /// </summary>
    private async Task RunDiscoveryAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running discovery: crawling SQL Server objects");

        try
        {
            var definitions = await _crawler.CrawlObjectsAsync(stoppingToken);
            _logger.LogInformation("Discovery: crawled {Count} objects", definitions.Count);

            var sessionId = await _api.PostDiscoveryAsync(definitions, stoppingToken);
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

    /// <summary>
    /// Processes a pending job. In the discovery-based flow, JobObjects are already created
    /// by the backend when the user selects objects from the dashboard.
    /// The agent only needs to: start the job, wait for Claude to finish, then benchmark.
    /// </summary>
    private async Task ProcessJobAsync(JobDto job, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Processing job {JobId}", job.Id);

        try
        {
            // Step 1 — signal the backend that we are picking up this job
            var started = await _api.StartJobAsync(job.Id, stoppingToken);
            if (!started)
            {
                _logger.LogError("Job {JobId}: failed to start job — aborting", job.Id);
                return;
            }

            _logger.LogInformation("Job {JobId}: started, waiting for optimization results", job.Id);

            // Step 2 — poll for optimized results (Claude runs independently on the backend)
            var results = await PollForResultsWithTimeoutAsync(job.Id, stoppingToken);
            if (results is null)
            {
                _logger.LogError(
                    "Job {JobId}: timed out after {Timeout} waiting for optimization results",
                    job.Id, ResultPollTimeout);
                return;
            }

            _logger.LogInformation("Job {JobId}: received {Count} optimized objects — submitting metrics",
                job.Id, results.Count);

            // Step 3 — execute each object and submit metrics
            foreach (var obj in results)
            {
                await SubmitObjectMetricsAsync(job.Id, obj, stoppingToken);
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
    /// Polls <see cref="BackendApiClient.PollForResultsAsync"/> at 10-second intervals
    /// until results arrive or the 30-minute timeout elapses.
    /// </summary>
    private async Task<List<JobObjectDto>?> PollForResultsWithTimeoutAsync(int jobId, CancellationToken stoppingToken)
    {
        var deadline = DateTime.UtcNow + ResultPollTimeout;

        while (DateTime.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            var results = await _api.PollForResultsAsync(jobId, stoppingToken);
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
    /// Executes both the original and optimized versions of a job object and posts
    /// the captured metrics to the backend.
    /// </summary>
    private async Task SubmitObjectMetricsAsync(int jobId, JobObjectDto obj, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Job {JobId}: executing [{Schema}].[{Object}] (TypeId={TypeId})",
            jobId, obj.SchemaName, obj.ObjectName, obj.ObjectTypeId);

        var defaultParamSet = obj.ParameterSets?.FirstOrDefault(p => p.IsDefault)
                              ?? obj.ParameterSets?.FirstOrDefault();
        var parametersJson  = defaultParamSet?.ParametersJson ?? string.Empty;
        var parameterSetId  = defaultParamSet?.Id ?? 0;

        var results = new List<ExecutionResultDto>();

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
                "Job {JobId}: original execution failed for [{Schema}].[{Object}] — skipping object",
                jobId, obj.SchemaName, obj.ObjectName);
            return;
        }

        // --- Optimized execution (only if the backend produced an optimized definition) ---
        if (!string.IsNullOrWhiteSpace(obj.OptimizedDefinition))
        {
            try
            {
                await _executor.DeployUnderOptimizerSchemaAsync(obj, obj.OptimizedDefinition, stoppingToken);

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
            }
            finally
            {
                try
                {
                    await _executor.RemoveFromOptimizerSchemaAsync(obj, stoppingToken);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx,
                        "Job {JobId}: cleanup of [optimizer].[{Object}] failed — may require manual removal",
                        jobId, obj.ObjectName);
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
            _logger.LogWarning(
                "Job {JobId}: failed to submit metrics for [{Schema}].[{Object}]",
                jobId, obj.SchemaName, obj.ObjectName);
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
                _reportedServerName   = serverInfo.ServerName;
                _reportedDatabaseName = serverInfo.DatabaseName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve SQL Server info for heartbeat — will retry next cycle");
            }
        }

        var heartbeat = new HeartbeatRequest
        {
            AgentId      = _config.AgentId,
            AgentVersion = GetAgentVersion(),
            MachineName  = Environment.MachineName,
            ServerName   = _reportedServerName,
            DatabaseName = _reportedDatabaseName,
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
}
