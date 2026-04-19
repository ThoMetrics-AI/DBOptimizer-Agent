namespace DbOptimizer.Agent.Crawling;

/// <summary>
/// The result of comparing original and optimized execution outputs for a single parameter set.
/// All comparison logic lives agent-side; only the derived boolean signals are posted to the backend.
/// </summary>
public record ExecutionValidationResult(
    bool RowCountMatch,
    bool ColumnSchemaMatch,
    bool? ValidationSkipped,
    string? SkipReason,
    bool IsDeterministic,
    bool? ChecksumMatch,
    bool? ChecksumExcludedImpreciseColumns);

/// <summary>
/// Compares original and optimized <see cref="CapturedMetrics"/> to produce an
/// <see cref="ExecutionValidationResult"/>. No row data is examined — all signals are derived
/// from metadata and hashes captured during execution.
/// </summary>
public static class ExecutionValidation
{
    /// <summary>
    /// Compares row count, column schema, and (when deterministic) result-set checksum between
    /// the original and optimized executions.
    /// </summary>
    /// <param name="original">Metrics from the original execution.</param>
    /// <param name="optimized">Metrics from the optimized execution.</param>
    /// <param name="isDeterministic">
    ///   Result of the pre-benchmark determinism probe on the original object.
    ///   When false, checksum comparison is skipped and <see cref="ExecutionValidationResult.ChecksumMatch"/>
    ///   is left null — a mismatch caused by non-determinism would be misleading.
    /// </param>
    public static ExecutionValidationResult Compare(
        CapturedMetrics original,
        CapturedMetrics optimized,
        bool isDeterministic)
    {
        // If either side produced no data result set, skip all comparison.
        if (original.ColumnSchema is null || optimized.ColumnSchema is null)
            return new ExecutionValidationResult(
                RowCountMatch:                   false,
                ColumnSchemaMatch:               false,
                ValidationSkipped:               true,
                SkipReason:                      "NoResultSet",
                IsDeterministic:                 isDeterministic,
                ChecksumMatch:                   null,
                ChecksumExcludedImpreciseColumns: null);

        // If the original is non-deterministic, checksum comparison would be meaningless.
        if (!isDeterministic)
            return new ExecutionValidationResult(
                RowCountMatch:                   false,
                ColumnSchemaMatch:               false,
                ValidationSkipped:               true,
                SkipReason:                      "NonDeterministic",
                IsDeterministic:                 false,
                ChecksumMatch:                   null,
                ChecksumExcludedImpreciseColumns: null);

        var rowCountMatch    = original.RowsReturned == optimized.RowsReturned;
        var columnSchemaMatch = SchemasMatch(original.ColumnSchema, optimized.ColumnSchema);

        // Checksum comparison — only meaningful when hashes are available.
        bool? checksumMatch = null;
        bool? checksumExcludedImprecise = null;

        if (original.ResultHash is not null && optimized.ResultHash is not null)
        {
            checksumMatch = original.ResultHash == optimized.ResultHash;
            // Report exclusion if either side excluded imprecise columns (they used the same schema).
            checksumExcludedImprecise =
                original.ChecksumExcludedImpreciseColumns || optimized.ChecksumExcludedImpreciseColumns;
        }

        return new ExecutionValidationResult(
            RowCountMatch:                   rowCountMatch,
            ColumnSchemaMatch:               columnSchemaMatch,
            ValidationSkipped:               null,
            SkipReason:                      null,
            IsDeterministic:                 true,
            ChecksumMatch:                   checksumMatch,
            ChecksumExcludedImpreciseColumns: checksumExcludedImprecise);
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

            // Best-effort type check: flag any mismatch (including widening) but Phase 2 does not block.
            if (!string.Equals(original[i].TypeName, optimized[i].TypeName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
