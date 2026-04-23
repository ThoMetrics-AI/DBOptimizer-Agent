namespace DbOptimizer.Agent.Http;

/// <summary>
/// Thrown by BackendApiClient when the backend rejects the agent's API key (401)
/// or returns a known 403 error code (AgentDisabled, OrgSuspended).
/// Propagates out of the poll loop so AgentWorker can stop the service gracefully.
/// </summary>
public sealed class AgentAuthException : Exception
{
    /// <summary>
    /// The error code from the response body (e.g. "InvalidApiKey", "AgentDisabled", "OrgSuspended").
    /// </summary>
    public string ErrorCode { get; }

    public AgentAuthException(string errorCode)
        : base($"Agent authentication rejected: {errorCode}")
    {
        ErrorCode = errorCode;
    }
}
