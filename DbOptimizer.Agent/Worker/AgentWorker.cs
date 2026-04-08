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

            if (poll.PendingJobs.Count > 0)
            {
                // Process one job per cycle — keeps each iteration bounded.
                var job = poll.PendingJobs[0];
                await ProcessJobAsync(job, stoppingToken);
            }
            else
            {
                await DelayAsync(_config.PollIntervalSeconds, stoppingToken);
            }
        }

        _logger.LogInformation("AgentWorker stopping.");
    }

    // -------------------------------------------------------------------------
    // Job processing
    // -------------------------------------------------------------------------

    private async Task ProcessJobAsync(JobDto job, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Processing job {JobId} (DatabaseConnectionId={DbConnId})",
            job.Id, job.DatabaseConnectionId);

        try
        {
            // Step 1 — crawl the customer database
            _logger.LogInformation("Job {JobId}: crawling SQL Server objects", job.Id);
            var definitions = await _crawler.CrawlObjectsAsync(stoppingToken);
            _logger.LogInformation("Job {JobId}: crawled {Count} objects", job.Id, definitions.Count);

            // Step 2 — submit definitions to backend (triggers Claude optimization)
            var submitted = await _api.SubmitObjectDefinitionsAsync(job.Id, definitions, stoppingToken);
            if (!submitted)
            {
                _logger.LogError("Job {JobId}: failed to submit object definitions — aborting job", job.Id);
                return;
            }

            _logger.LogInformation("Job {JobId}: definitions submitted, waiting for optimization results", job.Id);

            // Step 3 — poll for optimized results
            var results = await PollForResultsWithTimeoutAsync(job.Id, stoppingToken);
            if (results is null)
            {
                _logger.LogError(
                    "Job {JobId}: timed out after {Timeout} waiting for optimization results",
                    job.Id, ResultPollTimeout);
                return;
            }

            _logger.LogInformation("Job {JobId}: received {Count} optimized objects — submitting metrics", job.Id, results.Count);

            // Step 4 — execute each object and submit metrics
            foreach (var obj in results)
            {
                await SubmitObjectMetricsAsync(job.Id, obj, stoppingToken);
            }

            _logger.LogInformation("Job {JobId}: completed", job.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job {JobId}: cancelled", job.Id);
            throw; // propagate so the host can shut down cleanly
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

            _logger.LogDebug("Job {JobId}: results not ready, retrying in {Interval}s", jobId, ResultPollInterval.TotalSeconds);
            await Task.Delay(ResultPollInterval, stoppingToken);

            // Send heartbeat opportunistically while waiting for results.
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

                // Execute the optimized copy from the [optimizer] schema.
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
                // Always remove the optimizer-schema copy, even if execution failed.
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

        var heartbeat = new HeartbeatRequest
        {
            AgentId      = _config.AgentId,
            AgentVersion = GetAgentVersion(),
            MachineName  = Environment.MachineName,
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
