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
    bool? ChecksumExcludedImpreciseColumns,
    /// <summary>True when the checksum comparison used 3-band sampling instead of a full hash.</summary>
    bool? UsedSampledChecksum,
    /// <summary>
    /// True when excluded float/real/money columns have an average absolute difference below the
    /// configured epsilon.  Null when no float sample is available for comparison.
    /// </summary>
    bool? FloatColumnsApproximatelyEqual);

/// <summary>
/// Compares original and optimized <see cref="CapturedMetrics"/> to produce an
/// <see cref="ExecutionValidationResult"/>. No row data is examined — all signals are derived
/// from metadata and hashes captured during execution.
/// </summary>
public static class ExecutionValidation
{
    /// <summary>
    /// Compares row count, column schema, checksum, and (when imprecise columns were excluded)
    /// approximate float equality between the original and optimized executions.
    /// </summary>
    /// <param name="original">Metrics from the original execution.</param>
    /// <param name="optimized">Metrics from the optimized execution.</param>
    /// <param name="isDeterministic">
    ///   Result of the pre-benchmark determinism probe on the original object.
    ///   When false, checksum comparison is skipped.
    /// </param>
    /// <param name="floatEpsilon">
    ///   Maximum allowed average absolute difference when comparing excluded float columns.
    /// </param>
    public static ExecutionValidationResult Compare(
        CapturedMetrics original,
        CapturedMetrics optimized,
        bool isDeterministic,
        double floatEpsilon = 0.0001)
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
                ChecksumExcludedImpreciseColumns: null,
                UsedSampledChecksum:             null,
                FloatColumnsApproximatelyEqual:  null);

        // If the original is non-deterministic, checksum comparison would be meaningless.
        if (!isDeterministic)
            return new ExecutionValidationResult(
                RowCountMatch:                   false,
                ColumnSchemaMatch:               false,
                ValidationSkipped:               true,
                SkipReason:                      "NonDeterministic",
                IsDeterministic:                 false,
                ChecksumMatch:                   null,
                ChecksumExcludedImpreciseColumns: null,
                UsedSampledChecksum:             null,
                FloatColumnsApproximatelyEqual:  null);

        var rowCountMatch    = original.RowsReturned == optimized.RowsReturned;
        var columnSchemaMatch = SchemasMatch(original.ColumnSchema, optimized.ColumnSchema);

        // Checksum comparison.
        bool? checksumMatch = null;
        bool? checksumExcludedImprecise = null;
        bool? usedSampledChecksum = null;

        if (original.ResultHash is not null && optimized.ResultHash is not null)
        {
            checksumMatch = original.ResultHash == optimized.ResultHash;
            checksumExcludedImprecise =
                original.ChecksumExcludedImpreciseColumns || optimized.ChecksumExcludedImpreciseColumns;
            // Sampled when either side's result set exceeded the threshold and a sampled hash was used.
            usedSampledChecksum = original.ChecksumThresholdExceeded || optimized.ChecksumThresholdExceeded;
        }

        // Approximate float comparison — only when imprecise columns were excluded from the hash.
        bool? floatColumnsApproximatelyEqual = null;
        if ((original.ChecksumExcludedImpreciseColumns || optimized.ChecksumExcludedImpreciseColumns)
            && original.FloatColumnSample is { Count: > 0 }
            && optimized.FloatColumnSample is { Count: > 0 })
        {
            floatColumnsApproximatelyEqual = CompareFloatSamples(
                original.FloatColumnSample, optimized.FloatColumnSample, floatEpsilon);
        }

        return new ExecutionValidationResult(
            RowCountMatch:                   rowCountMatch,
            ColumnSchemaMatch:               columnSchemaMatch,
            ValidationSkipped:               null,
            SkipReason:                      null,
            IsDeterministic:                 true,
            ChecksumMatch:                   checksumMatch,
            ChecksumExcludedImpreciseColumns: checksumExcludedImprecise,
            UsedSampledChecksum:             usedSampledChecksum,
            FloatColumnsApproximatelyEqual:  floatColumnsApproximatelyEqual);
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

            if (!string.Equals(original[i].TypeName, optimized[i].TypeName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when the average absolute difference per float column across all sampled rows
    /// is within <paramref name="epsilon"/> for every column.
    /// Rows beyond the shorter sample are ignored.
    /// </summary>
    private static bool CompareFloatSamples(
        IReadOnlyList<double?[]> originalSample,
        IReadOnlyList<double?[]> optimizedSample,
        double epsilon)
    {
        int rows = Math.Min(originalSample.Count, optimizedSample.Count);
        if (rows == 0) return true;

        int cols = Math.Min(
            originalSample[0].Length,
            optimizedSample[0].Length);

        if (cols == 0) return true;

        for (int col = 0; col < cols; col++)
        {
            double totalDiff = 0;
            int compared = 0;

            for (int row = 0; row < rows; row++)
            {
                var origVal = originalSample[row][col];
                var optVal  = optimizedSample[row][col];

                if (origVal is null || optVal is null)
                    continue;

                totalDiff += Math.Abs(origVal.Value - optVal.Value);
                compared++;
            }

            if (compared == 0) continue;

            double avgDiff = totalDiff / compared;
            if (avgDiff > epsilon)
                return false;
        }

        return true;
    }
}
