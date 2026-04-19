using System.IO.Hashing;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbOptimizer.Agent.Crawling;

/// <summary>
/// Result returned by <see cref="ResultSetHasher.HashCurrentResultSetAsync"/>.
/// </summary>
internal record HashCurrentResultSetResult(
    /// <summary>XxHash64 of all included columns; null when the row threshold was exceeded.</summary>
    ulong? Hash,
    bool ExcludedImpreciseColumns,
    int RowCount,
    /// <summary>True when the row count exceeded the configured threshold and Hash is null.</summary>
    bool ThresholdExceeded,
    /// <summary>
    /// Up to <see cref="ResultSetHasher.FloatSampleLimit"/> rows of double? values for each
    /// imprecise-type column (float, real, money).  Null when no imprecise columns exist.
    /// The inner array is indexed by imprecise-column position within the result set.
    /// </summary>
    IReadOnlyList<double?[]>? FloatSample);

/// <summary>
/// Computes a streaming XxHash64 over a <see cref="SqlDataReader"/> result set.
/// Rows are processed one at a time — the full result set is never materialised in memory.
/// </summary>
internal static class ResultSetHasher
{
    // SQL Server type names whose values use floating-point storage and are excluded from the hash.
    private static readonly HashSet<string> ImpreciseTypes =
        new(StringComparer.OrdinalIgnoreCase) { "float", "real", "money" };

    // Separator byte sequences that prevent hash collisions across column/row boundaries.
    private static readonly byte[] ColumnSeparator = [0xFE, 0x01];
    private static readonly byte[] RowSeparator    = [0xFE, 0x02];
    private static readonly byte[] NullSentinel    = [0xFF, 0x00];

    /// <summary>Maximum number of rows captured in <see cref="HashCurrentResultSetResult.FloatSample"/>.</summary>
    internal const int FloatSampleLimit = 500;

    /// <summary>Number of rows sampled per band in <see cref="ComputeSampledHashAsync"/>.</summary>
    private const int SampleBandSize = 500;

    /// <summary>
    /// Returns true if <paramref name="dataTypeName"/> is a floating-point or money type
    /// that should be excluded from checksum computation.
    /// </summary>
    internal static bool IsImpreciseType(string dataTypeName) =>
        ImpreciseTypes.Contains(dataTypeName);

    /// <summary>
    /// Streams through every row of the reader's current result set, hashing each included
    /// column value.  Stops hashing (but continues counting) once <paramref name="threshold"/>
    /// rows are exceeded.  Concurrently captures up to <see cref="FloatSampleLimit"/> rows of
    /// float column values for approximate comparison.
    /// </summary>
    internal static async Task<HashCurrentResultSetResult> HashCurrentResultSetAsync(
        SqlDataReader reader,
        IReadOnlyList<(string Name, string TypeName)> schema,
        int threshold,
        ILogger logger,
        string objectName,
        CancellationToken cancellationToken)
    {
        // Pre-compute per-column inclusion flags and identify imprecise-column ordinals.
        var include = new bool[schema.Count];
        var impreciseOrdinals = new List<int>();
        bool excludedAny = false;

        for (int i = 0; i < schema.Count; i++)
        {
            if (ImpreciseTypes.Contains(schema[i].TypeName))
            {
                include[i] = false;
                excludedAny = true;
                impreciseOrdinals.Add(i);
                logger.LogDebug(
                    "Checksum [{Object}]: excluding column {Col} ({Type}) — imprecise floating-point type",
                    objectName, schema[i].Name, schema[i].TypeName);
            }
            else
            {
                include[i] = true;
            }
        }

        var hasher = new XxHash64();
        int rowCount = 0;
        bool thresholdExceeded = false;

        // Float sample — only populated when imprecise columns exist.
        List<double?[]>? floatSampleList = impreciseOrdinals.Count > 0 ? [] : null;

        while (await reader.ReadAsync(cancellationToken))
        {
            rowCount++;

            // Once the threshold is exceeded, switch to count-only mode.
            if (!thresholdExceeded && rowCount > threshold)
            {
                thresholdExceeded = true;
                logger.LogDebug(
                    "Checksum [{Object}]: row threshold {Threshold} exceeded at row {Row} — " +
                    "switching to count-only; sampled checksum will be computed separately",
                    objectName, threshold, rowCount);
            }

            // Hash included columns while below threshold.
            if (!thresholdExceeded)
            {
                for (int i = 0; i < schema.Count; i++)
                {
                    if (!include[i]) continue;

                    if (reader.IsDBNull(i))
                        hasher.Append(NullSentinel);
                    else
                        AppendColumnValue(ref hasher, reader, i);

                    hasher.Append(ColumnSeparator);
                }
                hasher.Append(RowSeparator);
            }

            // Capture float column values for the first FloatSampleLimit rows.
            if (floatSampleList is not null && rowCount <= FloatSampleLimit)
            {
                var row = new double?[impreciseOrdinals.Count];
                for (int j = 0; j < impreciseOrdinals.Count; j++)
                {
                    int col = impreciseOrdinals[j];
                    row[j] = reader.IsDBNull(col) ? null : ConvertToDouble(reader, col, schema[col].TypeName);
                }
                floatSampleList.Add(row);
            }
        }

        ulong? hash = thresholdExceeded ? null : hasher.GetCurrentHashAsUInt64();
        IReadOnlyList<double?[]>? floatSample = floatSampleList is { Count: > 0 } ? floatSampleList : null;

        return new HashCurrentResultSetResult(hash, excludedAny, rowCount, thresholdExceeded, floatSample);
    }

    /// <summary>
    /// Computes an XxHash64 over a 3-band statistical sample of the result set.
    /// Bands: first <see cref="SampleBandSize"/> rows, middle <see cref="SampleBandSize"/> rows,
    /// and last <see cref="SampleBandSize"/> rows.  Rows outside the bands are drained (skipped).
    /// <para>
    /// When <paramref name="totalRows"/> is small enough that bands overlap, all rows are hashed.
    /// </para>
    /// </summary>
    internal static async Task<ulong> ComputeSampledHashAsync(
        SqlDataReader reader,
        IReadOnlyList<(string Name, string TypeName)> schema,
        int totalRows,
        ILogger logger,
        string objectName,
        CancellationToken cancellationToken)
    {
        var include = new bool[schema.Count];
        for (int i = 0; i < schema.Count; i++)
            include[i] = !ImpreciseTypes.Contains(schema[i].TypeName);

        // Compute band boundaries (1-based row numbers).
        int bandSize = SampleBandSize;
        int band1End  = Math.Min(bandSize, totalRows);
        int band2Start = Math.Max(band1End + 1, totalRows / 2 - bandSize / 2);
        int band2End   = Math.Min(band2Start + bandSize - 1, totalRows);
        int band3Start = Math.Max(band2End + 1, totalRows - bandSize + 1);

        var hasher = new XxHash64();
        int rowNum = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            rowNum++;

            bool inBand1 = rowNum <= band1End;
            bool inBand2 = rowNum >= band2Start && rowNum <= band2End;
            bool inBand3 = rowNum >= band3Start;

            if (!inBand1 && !inBand2 && !inBand3)
                continue; // skip this row

            for (int i = 0; i < schema.Count; i++)
            {
                if (!include[i]) continue;

                if (reader.IsDBNull(i))
                    hasher.Append(NullSentinel);
                else
                    AppendColumnValue(ref hasher, reader, i);

                hasher.Append(ColumnSeparator);
            }
            hasher.Append(RowSeparator);
        }

        logger.LogDebug(
            "Sampled checksum [{Object}]: totalRows={Total}, bands=[1-{B1E}],[{B2S}-{B2E}],[{B3S}-end]",
            objectName, totalRows, band1End, band2Start, band2End, band3Start);

        return hasher.GetCurrentHashAsUInt64();
    }

    // -------------------------------------------------------------------------
    // Column value helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Appends a stable byte representation of the value at <paramref name="ordinal"/> to
    /// <paramref name="hasher"/>.  Numeric types use their underlying binary form; strings use
    /// UTF-8; everything else falls back to <c>ToString()</c> → UTF-8.
    /// </summary>
    private static void AppendColumnValue(ref XxHash64 hasher, SqlDataReader reader, int ordinal)
    {
        var clrType = reader.GetFieldType(ordinal);

        if (clrType == typeof(int))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetInt32(ordinal)));
        }
        else if (clrType == typeof(long))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetInt64(ordinal)));
        }
        else if (clrType == typeof(short))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetInt16(ordinal)));
        }
        else if (clrType == typeof(byte))
        {
            hasher.Append([reader.GetByte(ordinal)]);
        }
        else if (clrType == typeof(bool))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetBoolean(ordinal)));
        }
        else if (clrType == typeof(decimal))
        {
            // decimal.GetBits() returns four ints representing the exact binary form.
            foreach (var part in decimal.GetBits(reader.GetDecimal(ordinal)))
                hasher.Append(BitConverter.GetBytes(part));
        }
        else if (clrType == typeof(double))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetDouble(ordinal)));
        }
        else if (clrType == typeof(float))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetFloat(ordinal)));
        }
        else if (clrType == typeof(DateTime))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetDateTime(ordinal).Ticks));
        }
        else if (clrType == typeof(DateTimeOffset))
        {
            var dto = reader.GetDateTimeOffset(ordinal);
            // Hash both the UTC ticks and the offset so time zone changes are detected.
            hasher.Append(BitConverter.GetBytes(dto.UtcTicks));
            hasher.Append(BitConverter.GetBytes(dto.Offset.Ticks));
        }
        else if (clrType == typeof(TimeSpan))
        {
            hasher.Append(BitConverter.GetBytes(reader.GetFieldValue<TimeSpan>(ordinal).Ticks));
        }
        else if (clrType == typeof(Guid))
        {
            hasher.Append(reader.GetGuid(ordinal).ToByteArray());
        }
        else if (clrType == typeof(byte[]))
        {
            hasher.Append((byte[])reader.GetValue(ordinal));
        }
        else if (clrType == typeof(string))
        {
            hasher.Append(Encoding.UTF8.GetBytes(reader.GetString(ordinal)));
        }
        else
        {
            // Safe fallback for sql_variant, geometry, hierarchy, etc.
            var s = reader.GetValue(ordinal)?.ToString() ?? string.Empty;
            hasher.Append(Encoding.UTF8.GetBytes(s));
        }
    }

    private static double? ConvertToDouble(SqlDataReader reader, int ordinal, string typeName)
    {
        try
        {
            if (typeName.Equals("money", StringComparison.OrdinalIgnoreCase))
                return (double)reader.GetDecimal(ordinal);

            var clrType = reader.GetFieldType(ordinal);
            if (clrType == typeof(double)) return reader.GetDouble(ordinal);
            if (clrType == typeof(float))  return reader.GetFloat(ordinal);
            if (clrType == typeof(decimal)) return (double)reader.GetDecimal(ordinal);
            return null;
        }
        catch
        {
            return null;
        }
    }
}
