using System.Text.RegularExpressions;
using DbOptimizer.Contracts.Dtos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbOptimizer.Agent.Crawling;

/// <summary>
/// Server metadata returned by <see cref="SqlServerCrawler.GetServerInfoAsync"/>.
/// </summary>
public class ServerInfo
{
    public string ServerName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string SqlServerVersion { get; set; } = string.Empty;
    public int CompatibilityLevel { get; set; }
}

/// <summary>
/// Connects to the customer SQL Server and collects object definitions for the backend.
/// </summary>
public class SqlServerCrawler
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerCrawler> _logger;

    // Matches query hints: OPTION(RECOMPILE), OPTION(MAXDOP 4), etc.
    // Uses a non-greedy inner match to handle nested parens like OPTION(TABLE HINT(...)).
    private static readonly Regex OptionHintRegex = new(
        @"OPTION\s*\([^)]+(?:\([^)]*\)[^)]*)*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches table hints: WITH(NOLOCK), WITH (NOLOCK, ROWLOCK), etc.
    // Deliberately excludes CTE syntax (WITH cte AS ...) by requiring '(' immediately after WITH.
    private static readonly Regex TableHintRegex = new(
        @"WITH\s*\(\s*(?:NOLOCK|ROWLOCK|UPDLOCK|READPAST|XLOCK|TABLOCK|TABLOCKX|HOLDLOCK|" +
        @"READUNCOMMITTED|READCOMMITTED|REPEATABLEREAD|SERIALIZABLE|READCOMMITTEDLOCK|" +
        @"NOEXPAND|FORCESEEK|FORCESCAN)(?:\s*,\s*(?:NOLOCK|ROWLOCK|UPDLOCK|READPAST|XLOCK|" +
        @"TABLOCK|TABLOCKX|HOLDLOCK|READUNCOMMITTED|READCOMMITTED|REPEATABLEREAD|SERIALIZABLE|" +
        @"READCOMMITTEDLOCK|NOEXPAND|FORCESEEK|FORCESCAN))*\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // sys.objects type codes → ObjectTypeIds constants (inlined — DbOptimizer.Core not referenced by agent)
    private static readonly Dictionary<string, int> ObjectTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["P"]  = 1, // StoredProcedure
        ["V"]  = 2, // View
        ["FN"] = 3, // ScalarFunction
        ["IF"] = 4, // TableValuedFunction (inline)
        ["TF"] = 5, // MultiStatementTVF
    };

    public SqlServerCrawler(string connectionString, ILogger<SqlServerCrawler> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// Queries sys.objects, sys.sql_modules, and sys.schemas for all stored procedures,
    /// views, scalar functions, inline TVFs, and multi-statement TVFs.
    /// Also attempts to populate execution frequency from sys.dm_exec_procedure_stats.
    /// </summary>
    public async Task<List<DiscoveredObjectDto>> CrawlObjectsAsync(CancellationToken cancellationToken = default)
    {
        var serverInfo = await GetServerInfoAsync(cancellationToken);
        var frequencies = await GetProcedureFrequenciesAsync(cancellationToken);

        const string sql = """
            SELECT
                s.name       AS SchemaName,
                o.name       AS ObjectName,
                o.type       AS ObjectType,
                m.definition AS Definition
            FROM sys.objects o
            JOIN sys.sql_modules m ON m.object_id = o.object_id
            JOIN sys.schemas   s ON s.schema_id  = o.schema_id
            WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF')
              AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name
            """;

        var results = new List<DiscoveredObjectDto>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        int colSchema     = reader.GetOrdinal("SchemaName");
        int colObject     = reader.GetOrdinal("ObjectName");
        int colType       = reader.GetOrdinal("ObjectType");
        int colDefinition = reader.GetOrdinal("Definition");

        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(colSchema);
            var objectName = reader.GetString(colObject);
            var objectType = reader.GetString(colType).Trim();
            var definition = reader.GetString(colDefinition);

            if (!ObjectTypeMap.TryGetValue(objectType, out var objectTypeId))
            {
                _logger.LogWarning("Unknown object type '{Type}' for {Schema}.{Object} — skipping",
                    objectType, schemaName, objectName);
                continue;
            }

            frequencies.TryGetValue($"{schemaName}.{objectName}", out var frequency);
            var hints = ExtractHints(definition);

            results.Add(new DiscoveredObjectDto
            {
                SchemaName               = schemaName,
                ObjectName               = objectName,
                ObjectTypeId             = objectTypeId,
                Definition               = definition,
                SqlServerVersion         = serverInfo.SqlServerVersion,
                CompatibilityLevel       = serverInfo.CompatibilityLevel,
                ExistingHints            = hints.Length > 0 ? string.Join("; ", hints) : null,
                ExecutionFrequencyPerDay = frequency > 0 ? frequency : null,
                FrequencySourceId        = frequency > 0 ? 1 : null, // 1 = FrequencySourceIds.DatabaseDMV
            });
        }

        _logger.LogInformation(
            "Crawled {Count} objects from {Server}/{Database}",
            results.Count, serverInfo.ServerName, serverInfo.DatabaseName);

        return results;
    }

    /// <summary>
    /// Returns server name, database name, SQL Server version string, and compatibility level.
    /// </summary>
    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                @@SERVERNAME                                                      AS ServerName,
                DB_NAME()                                                         AS DatabaseName,
                @@VERSION                                                         AS SqlServerVersion,
                CAST(DATABASEPROPERTYEX(DB_NAME(), 'CompatibilityLevel') AS INT) AS CompatibilityLevel
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Could not retrieve server info from SQL Server.");

        return new ServerInfo
        {
            ServerName       = reader.GetString(reader.GetOrdinal("ServerName")),
            DatabaseName     = reader.GetString(reader.GetOrdinal("DatabaseName")),
            SqlServerVersion = reader.GetString(reader.GetOrdinal("SqlServerVersion")),
            CompatibilityLevel = reader.GetInt32(reader.GetOrdinal("CompatibilityLevel")),
        };
    }

    /// <summary>
    /// Queries sys.dm_exec_procedure_stats for estimated executions per day.
    /// Returns empty dictionary if the DMV is inaccessible (permissions or Express edition).
    /// Only covers stored procedures — views and functions are not tracked by this DMV.
    /// </summary>
    private async Task<Dictionary<string, decimal>> GetProcedureFrequenciesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.name AS SchemaName,
                o.name AS ObjectName,
                CAST(SUM(ps.execution_count) AS DECIMAL(18, 4))
                    / NULLIF(DATEDIFF(DAY, MIN(ps.cached_time), GETDATE()), 0) AS ExecutionsPerDay
            FROM sys.dm_exec_procedure_stats ps
            JOIN sys.objects o ON o.object_id = ps.object_id
            JOIN sys.schemas s ON s.schema_id  = o.schema_id
            WHERE o.is_ms_shipped = 0
            GROUP BY s.name, o.name
            """;

        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            int colSchema = reader.GetOrdinal("SchemaName");
            int colObject = reader.GetOrdinal("ObjectName");
            int colFreq   = reader.GetOrdinal("ExecutionsPerDay");

            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(colFreq)) continue;

                var key  = $"{reader.GetString(colSchema)}.{reader.GetString(colObject)}";
                result[key] = reader.GetDecimal(colFreq);
            }

            _logger.LogDebug("Retrieved execution frequencies for {Count} procedures", result.Count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex,
                "Could not query sys.dm_exec_procedure_stats — execution frequencies will not be populated. " +
                "Ensure the agent's SQL login has VIEW SERVER STATE permission.");
        }

        return result;
    }

    /// <summary>
    /// Extracts distinct query hints (OPTION clauses) and table hints (WITH clauses) from a T-SQL definition.
    /// </summary>
    private static string[] ExtractHints(string definition)
    {
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hints = new List<string>();

        foreach (Match m in OptionHintRegex.Matches(definition))
        {
            if (seen.Add(m.Value))
                hints.Add(m.Value);
        }

        foreach (Match m in TableHintRegex.Matches(definition))
        {
            if (seen.Add(m.Value))
                hints.Add(m.Value);
        }

        return [.. hints];
    }
}
