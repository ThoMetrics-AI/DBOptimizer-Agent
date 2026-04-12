namespace DbOptimizer.Agent.Configuration;

public class AgentConfiguration
{
    public const string SectionName = "Agent";

    /// <summary>
    /// The base URL of the SqlBrain backend API.
    /// Example: https://api.sqlbrain.ai
    /// </summary>
    public string BackendUrl { get; set; } = string.Empty;

    /// <summary>
    /// The agent's API key issued by the SqlBrain backend on registration.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The connection string to the customer's SQL Server instance.
    /// </summary>
    public string SqlConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// How often the agent polls the backend for new jobs (in seconds).
    /// Default: 15 seconds.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How often the agent sends a heartbeat to the backend (in seconds).
    /// Default: 60 seconds.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Timeout for HTTP requests to the backend (in seconds).
    /// Default: 30 seconds.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 30;
}
