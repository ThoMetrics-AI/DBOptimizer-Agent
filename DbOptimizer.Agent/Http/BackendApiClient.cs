using System.Net.Http.Json;
using DbOptimizer.Agent.Configuration;
using DbOptimizer.Contracts.Dtos;
using DbOptimizer.Contracts.Requests;
using DbOptimizer.Contracts.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbOptimizer.Agent.Http;

public class BackendApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AgentConfiguration _config;
    private readonly ILogger<BackendApiClient> _logger;

    public BackendApiClient(HttpClient httpClient, IOptions<AgentConfiguration> config, ILogger<BackendApiClient> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_config.BackendUrl.TrimEnd('/') + '/');
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.HttpTimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Add("X-Agent-ApiKey", _config.ApiKey);
    }

    /// <summary>
    /// Polls the backend for pending work.
    /// Returns null if the response cannot be read.
    /// </summary>
    public async Task<AgentPollResponse?> PollForJobAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            var response = await _httpClient.GetAsync($"api/agent/poll?agentVersion={version}", cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<AgentPollResponse>(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error polling backend for job");
            return null;
        }
    }

    /// <summary>
    /// Posts discovered objects to the backend to create a DiscoverySession.
    /// Returns the new session ID, or null on failure.
    /// </summary>
    public async Task<int?> PostDiscoveryAsync(List<DiscoveredObjectDto> objects, CancellationToken cancellationToken)
    {
        try
        {
            var request = new PostAgentDiscoveryRequest { Objects = objects };
            var response = await _httpClient.PostAsJsonAsync("api/agent/discovery", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<AgentDiscoveryResponse>(cancellationToken: cancellationToken);
            return result?.DiscoverySessionId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error posting discovery results");
            return null;
        }
    }

    /// <summary>
    /// Signals the backend that the agent is starting a pending job.
    /// Transitions the job from Pending to Running.
    /// </summary>
    public async Task<bool> StartJobAsync(int jobId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/agent/jobs/{jobId}/start", null, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error starting job {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// Polls the backend for optimized results ready for execution.
    /// Returns null if no results are ready yet (204 NoContent).
    /// </summary>
    public async Task<List<JobObjectDto>?> PollForResultsAsync(int jobId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/agent/jobs/{jobId}/results", cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<JobObjectDto>>(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error polling for results for job {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Submits execution metrics for a job object back to the backend.
    /// </summary>
    public async Task<bool> SubmitMetricsAsync(int jobId, PostExecutionResultsRequest metrics, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"api/agent/jobs/{jobId}/metrics", metrics, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error submitting metrics for job {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// Sends a heartbeat to the backend to signal the agent is alive.
    /// </summary>
    public async Task<bool> SendHeartbeatAsync(HeartbeatRequest heartbeat, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/agent/heartbeat", heartbeat, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error sending heartbeat");
            return false;
        }
    }
}
