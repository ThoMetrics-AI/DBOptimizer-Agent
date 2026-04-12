using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

    // Regex to detect stored procedure parameters (lines containing @paramName type).
    private static readonly Regex SpParamRegex = new(
        @"@[A-Za-z_][A-Za-z0-9_]*\s+\w",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            if (poll.RunDiscovery)
            {
                await RunDiscoveryAsync(stoppingToken);
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

                foreach (var obj in poll.ReadyToExecuteObjects)
                {
                    await SubmitObjectMetricsAsync(obj.JobId, obj, stoppingToken);
                }
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

            _logger.LogInformation("Job {JobId}: started, waiting for optimization results", job.Id);

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

        const int StoredProcedureTypeId = 1; // ObjectTypeIds.StoredProcedure (inlined — Core not referenced)

        // Change 5: Detect missing parameter sets for stored procedures.
        if (obj.ObjectTypeId == StoredProcedureTypeId)
        {
            var hasParamSets = obj.ParameterSets?.Count > 0;
            if (!hasParamSets)
            {
                var definition = obj.OriginalDefinition ?? string.Empty;
                if (DefinitionContainsParameters(definition))
                {
                    _logger.LogError(
                        "Job {JobId}: [{Schema}].[{Object}] is a stored procedure with parameters but no ParameterSet — cannot execute, marking failed",
                        jobId, obj.SchemaName, obj.ObjectName);

                    await _api.ReportExecutionFailedAsync(jobId, obj.Id, "NoParameterSet", stoppingToken);
                    return;
                }
            }
        }

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
                    // Optimized execution failure is non-fatal: we still post the original metrics.
                }
                finally
                {
                    // Optimizer schema cleanup always runs, even if optimized execution failed.
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

    /// <summary>
    /// Returns true if the stored procedure definition contains at least one parameter declaration
    /// (e.g. @CustomerId INT or @Name NVARCHAR(100)).
    /// </summary>
    private static bool DefinitionContainsParameters(string definition)
        => SpParamRegex.IsMatch(definition);

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
