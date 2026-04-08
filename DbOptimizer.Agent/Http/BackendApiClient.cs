using System.Net.Http.Headers;
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
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("ApiKey", _config.ApiKey);
    }

    /// <summary>
    /// Polls the backend for a pending job assigned to this agent.
    /// Returns null if no job is available.
    /// </summary>
    public async Task<AgentPollResponse?> PollForJobAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("api/agent/poll", cancellationToken);

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
    /// Submits crawled object definitions to the backend for a given job.
    /// </summary>
    public async Task<bool> SubmitObjectDefinitionsAsync(int jobId, List<DiscoveredObjectDto> definitions, CancellationToken cancellationToken)
    {
        try
        {
            var request = new PostDiscoveredObjectsRequest { JobId = jobId, Objects = definitions };
            var response = await _httpClient.PostAsJsonAsync($"api/agent/jobs/{jobId}/definitions", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error submitting object definitions for job {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// Polls the backend for optimized results ready for execution.
    /// Returns null if no results are ready yet.
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
