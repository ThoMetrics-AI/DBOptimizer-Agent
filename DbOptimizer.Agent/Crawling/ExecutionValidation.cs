namespace DbOptimizer.Agent.Crawling;

/// <summary>
/// The result of comparing original and optimized execution outputs for a single parameter set.
/// All comparison logic lives agent-side; only the derived boolean signals are posted to the backend.
/// </summary>
public record ExecutionValidationResult(
    bool RowCountMatch,
    bool ColumnSchemaMatch,
    bool? ValidationSkipped,
    string? SkipReason);

/// <summary>
/// Compares original and optimized <see cref="CapturedMetrics"/> to produce an
/// <see cref="ExecutionValidationResult"/>. No row data is examined — comparison uses only
/// the row count already captured and the column schema read from reader metadata.
/// </summary>
public static class ExecutionValidation
{
    /// <summary>
    /// Compares row count and column schema between the original and optimized executions.
    /// Returns a skipped result when either execution produced no data result set (e.g. void procs,
    /// procs that only INSERT into #temp and return nothing).
    /// </summary>
    public static ExecutionValidationResult Compare(CapturedMetrics original, CapturedMetrics optimized)
    {
        // If either side produced no data result set, validation cannot run.
        if (original.ColumnSchema is null || optimized.ColumnSchema is null)
            return new ExecutionValidationResult(
                RowCountMatch:     false,
                ColumnSchemaMatch: false,
                ValidationSkipped: true,
                SkipReason:        "NoResultSet");

        var rowCountMatch = original.RowsReturned == optimized.RowsReturned;
        var columnSchemaMatch = SchemasMatch(original.ColumnSchema, optimized.ColumnSchema);

        return new ExecutionValidationResult(
            RowCountMatch:     rowCountMatch,
            ColumnSchemaMatch: columnSchemaMatch,
            ValidationSkipped: null,
            SkipReason:        null);
    }

    private static bool SchemasMatch(
        IReadOnlyList<(string Name, string TypeName)> original,
        IReadOnlyList<(string Name, string TypeName)> optimized)
    {
        if (original.Count != optimized.Count)
            return false;

        for (int i = 0; i < original.Count; i++)
        {
            if (!string.Equals(original[i].Name, optimized[i].Name, StringComparison.OrdinalIgnoreCase))
                return false;

            // Best-effort type check: flag any mismatch (including widening) but Phase 1 does not block.
            if (!string.Equals(original[i].TypeName, optimized[i].TypeName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
