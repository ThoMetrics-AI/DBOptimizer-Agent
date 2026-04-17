using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DbOptimizer.Agent.Configuration;
using DbOptimizer.Contracts.Dtos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbOptimizer.Agent.Crawling;

/// <summary>
/// Performance metrics captured from a single execution of a SQL object.
/// </summary>
public record CapturedMetrics(
    long ExecutionMs,
    long LogicalReads,
    long CpuTimeMs,
    int RowsReturned,
    string? ExecutionPlanXml,
    IReadOnlyList<string> MissingIndexSuggestions);

/// <summary>
/// Executes stored procedures, views, and functions against the customer's SQL Server
/// and captures execution plan and performance statistics.
/// </summary>
public class SqlObjectExecutor
{
    private readonly string _connectionString;
    private readonly ILogger<SqlObjectExecutor> _logger;

    // ObjectTypeId constants — must match ObjectTypeMap in SqlServerCrawler.
    private const int StoredProcedure  = 1;
    private const int View             = 2;
    private const int ScalarFunction   = 3;
    private const int InlineTVF        = 4;
    private const int MultiStatementTVF = 5;

    // "Table 'X'. Scan count N, logical reads 5, ..."
    private static readonly Regex LogicalReadsRegex = new(
        @"logical reads (\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "SQL Server Execution Times:\r\n   CPU time = 16 ms,  elapsed time = 42 ms."
    private static readonly Regex CpuTimeRegex = new(
        @"CPU time = (\d+) ms",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches the CREATE/ALTER header through the schema-qualified object name.
    // Groups: (1) = CREATE [OR ALTER] / ALTER, (2) = keyword, full schema.name match consumed.
    private static readonly Regex ObjectHeaderRegex = new(
        @"(CREATE\s+(?:OR\s+ALTER\s+)?|ALTER\s+)(PROCEDURE|PROC|VIEW|FUNCTION)\s+(?:\[?\w+\]?\s*\.\s*)?\[?\w+\]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SqlObjectExecutor(AgentConfiguration configuration, ILogger<SqlObjectExecutor> logger)
    {
        _connectionString = configuration.SqlConnectionString;
        _logger = logger;
    }

    /// <summary>
    /// Executes the object with the supplied parameters under STATISTICS IO/TIME/XML ON
    /// and returns captured performance metrics including the actual execution plan.
    /// </summary>
    public async Task<CapturedMetrics> ExecuteAndCaptureAsync(
        JobObjectDto jobObject,
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        var parameters = ParseParameters(parametersJson);
        var infoMessages = new List<string>();

        await using var connection = new SqlConnection(_connectionString);
        connection.InfoMessage += (_, e) => infoMessages.Add(e.Message);
        await connection.OpenAsync(cancellationToken);

        const string statsSql = """
            SET STATISTICS IO ON;
            SET STATISTICS TIME ON;
            SET STATISTICS XML ON;
            """;

        await using (var statsCmd = new SqlCommand(statsSql, connection))
        {
            await statsCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        using var execCmd = BuildExecuteCommand(jobObject, parameters, connection);
        execCmd.CommandTimeout = 120;

        string? planXml = null;
        int rowsReturned = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var reader = await execCmd.ExecuteReaderAsync(cancellationToken);

        do
        {
            // The XML plan result set has a single column whose name contains "XML Showplan".
            if (reader.FieldCount == 1 &&
                reader.GetName(0).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase))
            {
                if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
                    planXml = reader.GetString(0);

                continue;
            }

            while (await reader.ReadAsync(cancellationToken))
                rowsReturned++;
        }
        while (await reader.NextResultAsync(cancellationToken));

        sw.Stop();

        long logicalReads = SumRegexCaptures(LogicalReadsRegex, infoMessages);
        long cpuTimeMs    = SumRegexCaptures(CpuTimeRegex, infoMessages);
        var  missingIndexes = ParseMissingIndexes(planXml);

        _logger.LogDebug(
            "Executed [{Schema}].[{Object}]: {ElapsedMs}ms elapsed, {CpuMs}ms CPU, " +
            "{LogicalReads} logical reads, {Rows} rows returned",
            jobObject.SchemaName, jobObject.ObjectName,
            sw.ElapsedMilliseconds, cpuTimeMs, logicalReads, rowsReturned);

        return new CapturedMetrics(
            ExecutionMs:            sw.ElapsedMilliseconds,
            LogicalReads:           logicalReads,
            CpuTimeMs:              cpuTimeMs,
            RowsReturned:           rowsReturned,
            ExecutionPlanXml:       planXml,
            MissingIndexSuggestions: missingIndexes);
    }

    /// <summary>
    /// Creates the [optimizer] schema if absent, rewrites the object header to target
    /// [optimizer].[objectName], then executes as CREATE OR ALTER.
    /// </summary>
    public async Task DeployUnderOptimizerSchemaAsync(
        JobObjectDto jobObject,
        string optimizedDefinition,
        CancellationToken cancellationToken = default)
    {
        const string ensureSchemaSql = """
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'optimizer')
                EXEC('CREATE SCHEMA [optimizer]');
            """;

        var rewrittenDefinition = RewriteObjectHeader(jobObject, optimizedDefinition);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var schemaCmd = new SqlCommand(ensureSchemaSql, connection))
        {
            await schemaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var deployCmd = new SqlCommand(rewrittenDefinition, connection)
        {
            CommandTimeout = 60
        };
        await deployCmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Deployed [optimizer].[{ObjectName}] (TypeId={ObjectTypeId})",
            jobObject.ObjectName, jobObject.ObjectTypeId);
    }

    /// <summary>
    /// Drops the object from the [optimizer] schema via DROP [TYPE] IF EXISTS.
    /// </summary>
    public async Task RemoveFromOptimizerSchemaAsync(
        JobObjectDto jobObject,
        CancellationToken cancellationToken = default)
    {
        var dropSql = BuildDropSql(jobObject);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var dropCmd = new SqlCommand(dropSql, connection)
        {
            CommandTimeout = 30
        };
        await dropCmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Dropped [optimizer].[{ObjectName}] from optimizer schema",
            jobObject.ObjectName);
    }

    // -------------------------------------------------------------------------
    // Command building
    // -------------------------------------------------------------------------

    private static SqlCommand BuildExecuteCommand(
        JobObjectDto jobObject,
        Dictionary<string, object?> parameters,
        SqlConnection connection)
    {
        var schema = EscapeIdentifier(jobObject.SchemaName);
        var name   = EscapeIdentifier(jobObject.ObjectName);
        var fullName = $"{schema}.{name}";

        return jobObject.ObjectTypeId switch
        {
            StoredProcedure                     => BuildSpCommand(fullName, parameters, connection),
            View                                => new SqlCommand($"SELECT * FROM {fullName}", connection),
            ScalarFunction                      => BuildFunctionCommand(fullName, parameters, connection, scalar: true),
            InlineTVF or MultiStatementTVF      => BuildFunctionCommand(fullName, parameters, connection, scalar: false),
            _ => throw new InvalidOperationException($"Unsupported ObjectTypeId: {jobObject.ObjectTypeId}")
        };
    }

    private static SqlCommand BuildSpCommand(
        string fullName,
        Dictionary<string, object?> parameters,
        SqlConnection connection)
    {
        var cmd = new SqlCommand(fullName, connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };

        foreach (var (name, value) in parameters)
        {
            var paramName = name.StartsWith('@') ? name : $"@{name}";
            cmd.Parameters.AddWithValue(paramName, value ?? DBNull.Value);
        }

        return cmd;
    }

    private static SqlCommand BuildFunctionCommand(
        string fullName,
        Dictionary<string, object?> parameters,
        SqlConnection connection,
        bool scalar)
    {
        var cmd = new SqlCommand(string.Empty, connection);
        var placeholders = new List<string>(parameters.Count);
        int i = 0;

        foreach (var (_, value) in parameters)
        {
            var p = $"@p{i++}";
            placeholders.Add(p);
            cmd.Parameters.AddWithValue(p, value ?? DBNull.Value);
        }

        var args = string.Join(", ", placeholders);
        // For scalar functions, wrap in a subquery to avoid SQL Server error 4121.
        // "SELECT [schema].[fn](args)" fails with non-dbo schemas because SQL Server
        // ambiguously interprets [schema] as a column name in the outer SELECT list.
        // The subquery forces schema resolution, eliminating the ambiguity.
        cmd.CommandText = scalar
            ? $"SELECT (SELECT {fullName}({args}))"
            : $"SELECT * FROM {fullName}({args})";

        return cmd;
    }

    // -------------------------------------------------------------------------
    // Rewrite / drop helpers
    // -------------------------------------------------------------------------

    private static string RewriteObjectHeader(JobObjectDto jobObject, string definition)
    {
        var keyword    = GetObjectKeyword(jobObject.ObjectTypeId);
        var objectName = EscapeIdentifier(jobObject.ObjectName);

        if (!ObjectHeaderRegex.IsMatch(definition))
            throw new InvalidOperationException(
                $"Could not locate a CREATE/ALTER {keyword} header in the definition of {objectName}. " +
                "Unable to rewrite the header for deployment to the [optimizer] schema.");

        // Replace the first CREATE/ALTER ... PROCEDURE/FUNCTION/VIEW [schema].[name]
        // with CREATE OR ALTER KEYWORD [optimizer].[name].
        return ObjectHeaderRegex.Replace(
            definition,
            _ => $"CREATE OR ALTER {keyword} [optimizer].{objectName}",
            count: 1);
    }

    private static string BuildDropSql(JobObjectDto jobObject)
    {
        var objectName = EscapeIdentifier(jobObject.ObjectName);
        var dropType = GetObjectKeyword(jobObject.ObjectTypeId);
        return $"DROP {dropType} IF EXISTS [optimizer].{objectName}";
    }

    private static string GetObjectKeyword(int objectTypeId) => objectTypeId switch
    {
        StoredProcedure                          => "PROCEDURE",
        View                                     => "VIEW",
        ScalarFunction or InlineTVF or MultiStatementTVF => "FUNCTION",
        _ => throw new InvalidOperationException($"Unsupported ObjectTypeId: {objectTypeId}")
    };

    private static string EscapeIdentifier(string name)
    {
        name = name.Trim('[', ']');
        return $"[{name.Replace("]", "]]")}]";
    }

    // -------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------

    private static Dictionary<string, object?> ParseParameters(string parametersJson)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(parametersJson))
            return result;

        using var doc = JsonDocument.Parse(parametersJson);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                JsonValueKind.Null   => null,
                _                   => prop.Value.GetRawText()
            };
        }

        return result;
    }

    private static long SumRegexCaptures(Regex regex, List<string> messages)
    {
        long total = 0;

        foreach (var msg in messages)
        {
            foreach (Match m in regex.Matches(msg))
            {
                if (long.TryParse(m.Groups[1].Value, out var n))
                    total += n;
            }
        }

        return total;
    }

    private static IReadOnlyList<string> ParseMissingIndexes(string? planXml)
    {
        if (string.IsNullOrWhiteSpace(planXml))
            return [];

        var suggestions = new List<string>();

        try
        {
            var doc = XDocument.Parse(planXml);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

            foreach (var group in doc.Descendants(ns + "MissingIndexGroup"))
            {
                var impact = group.Attribute("Impact")?.Value ?? "?";

                foreach (var index in group.Elements(ns + "MissingIndex"))
                {
                    var table  = index.Attribute("Table")?.Value  ?? "";
                    var schema = index.Attribute("Schema")?.Value ?? "";

                    var equalityCols = ColNames(index, ns, "EQUALITY");
                    var inequalityCols = ColNames(index, ns, "INEQUALITY");
                    var includeCols  = ColNames(index, ns, "INCLUDE");

                    var keyCols = new List<string>();
                    if (equalityCols.Count > 0)   keyCols.Add(string.Join(", ", equalityCols));
                    if (inequalityCols.Count > 0)  keyCols.Add(string.Join(", ", inequalityCols));

                    var suggestion = $"Impact {impact}%: CREATE INDEX ON {schema}.{table} ({string.Join(", ", keyCols)})";
                    if (includeCols.Count > 0)
                        suggestion += $" INCLUDE ({string.Join(", ", includeCols)})";

                    suggestions.Add(suggestion);
                }
            }
        }
        catch (Exception)
        {
            // Non-fatal — plan XML parsing failure must not abort metric capture.
        }

        return suggestions;
    }

    private static List<string> ColNames(XElement indexElement, XNamespace ns, string usage)
        => indexElement
            .Elements(ns + "ColumnGroup")
            .Where(g => g.Attribute("Usage")?.Value == usage)
            .SelectMany(g => g.Elements(ns + "Column"))
            .Select(c => c.Attribute("Name")?.Value ?? "")
            .Where(n => n.Length > 0)
            .ToList();
}
