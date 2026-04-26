using System.Diagnostics;
using System.Security.Cryptography;
using DbOptimizer.Agent.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbOptimizer.Agent.Crawling;

/// <summary>
/// Runs the seven-step capability check to prove the agent has sufficient
/// SQL Server permissions for audit operations. Each step is independent
/// and captures its own result. A throwaway schema is used for steps 4-7
/// and is always cleaned up in a finally block.
/// </summary>
public class CapabilityChecker
{
    private readonly string _connectionString;
    private readonly ILogger<CapabilityChecker> _logger;

    public CapabilityChecker(string connectionString, ILogger<CapabilityChecker> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<StepResult>> RunAllAsync(CancellationToken ct)
    {
        var schemaName = $"dbopt_preflight_{RandomHex(6)}";
        var results = new List<StepResult>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Steps 1-3: read-only checks
        results.Add(await RunStepAsync(1, "ReadDefinitions", () => CheckReadDefinitionsAsync(connection, ct)));
        results.Add(await RunStepAsync(2, "CaptureEstimatedPlans", () => CheckCaptureEstimatedPlansAsync(connection, ct)));
        results.Add(await RunStepAsync(3, "ReadDmvs", () => CheckReadDmvsAsync(connection, ct)));

        // Steps 4-7: schema operations (always clean up)
        try
        {
            results.Add(await RunStepAsync(4, "CreateSchema", () => CheckCreateSchemaAsync(connection, schemaName, ct)));
            results.Add(await RunStepAsync(5, "CreateObjectsInSchema", () => CheckCreateObjectAsync(connection, schemaName, ct)));
            results.Add(await RunStepAsync(6, "ExecuteObjects", () => CheckExecuteObjectAsync(connection, schemaName, ct)));
            results.Add(await RunStepAsync(7, "DropSchema", () => CheckDropSchemaAsync(connection, schemaName, ct)));
        }
        finally
        {
            await CleanupSchemaAsync(connection, schemaName);
        }

        return results;
    }

    private async Task<StepResult> RunStepAsync(int stepId, string stepName, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            sw.Stop();
            _logger.LogInformation("Capability check step {StepName} passed ({DurationMs}ms)", stepName, sw.ElapsedMilliseconds);
            return new StepResult(stepId, true, null, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Capability check step {StepName} failed", stepName);
            return new StepResult(stepId, false, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    private static async Task CheckReadDefinitionsAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT TOP 1 definition FROM sys.sql_modules", connection);
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
    }

    private static async Task CheckCaptureEstimatedPlansAsync(SqlConnection connection, CancellationToken ct)
    {
        // Use a separate connection since SET SHOWPLAN_XML ON changes session state
        await using var planConn = new SqlConnection(connection.ConnectionString);
        await planConn.OpenAsync(ct);

        await using var setOn = new SqlCommand("SET SHOWPLAN_XML ON", planConn);
        setOn.CommandTimeout = 10;
        await setOn.ExecuteNonQueryAsync(ct);

        await using var cmd = new SqlCommand("SELECT 1", planConn);
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("No execution plan returned.");
        var plan = reader.GetString(0);
        if (!plan.Contains("ShowPlanXML"))
            throw new InvalidOperationException("Invalid execution plan format.");
    }

    private static async Task CheckReadDmvsAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT TOP 1 * FROM sys.dm_exec_procedure_stats", connection);
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        // Just needs to not throw — empty result set is fine
    }

    private static async Task CheckCreateSchemaAsync(SqlConnection connection, string schemaName, CancellationToken ct)
    {
        var escaped = EscapeIdentifier(schemaName);
        await using var cmd = new SqlCommand($"CREATE SCHEMA {escaped}", connection);
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CheckCreateObjectAsync(SqlConnection connection, string schemaName, CancellationToken ct)
    {
        var schema = EscapeIdentifier(schemaName);
        var sql = $"CREATE PROCEDURE {schema}.[noop] AS SELECT 1 AS Result";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CheckExecuteObjectAsync(SqlConnection connection, string schemaName, CancellationToken ct)
    {
        var schema = EscapeIdentifier(schemaName);
        await using var cmd = new SqlCommand($"EXEC {schema}.[noop]", connection);
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CheckDropSchemaAsync(SqlConnection connection, string schemaName, CancellationToken ct)
    {
        var schema = EscapeIdentifier(schemaName);
        await using var dropProc = new SqlCommand($"DROP PROCEDURE IF EXISTS {schema}.[noop]", connection);
        dropProc.CommandTimeout = 30;
        await dropProc.ExecuteNonQueryAsync(ct);

        await using var dropSchema = new SqlCommand($"DROP SCHEMA IF EXISTS {schema}", connection);
        dropSchema.CommandTimeout = 30;
        await dropSchema.ExecuteNonQueryAsync(ct);
    }

    private async Task CleanupSchemaAsync(SqlConnection connection, string schemaName)
    {
        try
        {
            var schema = EscapeIdentifier(schemaName);

            await using var dropProc = new SqlCommand($"DROP PROCEDURE IF EXISTS {schema}.[noop]", connection);
            dropProc.CommandTimeout = 10;
            await dropProc.ExecuteNonQueryAsync();

            await using var dropSchema = new SqlCommand($"DROP SCHEMA IF EXISTS {schema}", connection);
            dropSchema.CommandTimeout = 10;
            await dropSchema.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up preflight schema {SchemaName}", schemaName);
        }
    }

    private static string EscapeIdentifier(string name)
    {
        name = name.Trim('[', ']');
        return $"[{name.Replace("]", "]]")}]";
    }

    private static string RandomHex(int bytes)
    {
        var buffer = new byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    public record StepResult(int StepId, bool Passed, string? ErrorMessage, int? DurationMs);
}
