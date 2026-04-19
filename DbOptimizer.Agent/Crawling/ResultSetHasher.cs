using System.IO.Hashing;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbOptimizer.Agent.Crawling;

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

    /// <summary>
    /// Returns true if <paramref name="dataTypeName"/> is a floating-point or money type
    /// that should be excluded from checksum computation.
    /// </summary>
    internal static bool IsImpreciseType(string dataTypeName) =>
        ImpreciseTypes.Contains(dataTypeName);

    /// <summary>
    /// Streams through every row of the reader's current result set, hashing each included
    /// column value into an XxHash64 instance.  Columns whose SQL type appears in
    /// <see cref="ImpreciseTypes"/> are skipped and logged at Debug level.
    /// </summary>
    /// <returns>
    /// The final XxHash64 value, whether any imprecise columns were excluded, and the total
    /// row count consumed from the result set.  The reader is fully drained on return.
    /// </returns>
    internal static async Task<(ulong Hash, bool ExcludedImpreciseColumns, int RowCount)>
        HashCurrentResultSetAsync(
            SqlDataReader reader,
            IReadOnlyList<(string Name, string TypeName)> schema,
            ILogger logger,
            string objectName,
            CancellationToken cancellationToken)
    {
        // Pre-compute per-column inclusion flags to avoid repeated set lookups in the hot loop.
        var include = new bool[schema.Count];
        bool excludedAny = false;

        for (int i = 0; i < schema.Count; i++)
        {
            if (ImpreciseTypes.Contains(schema[i].TypeName))
            {
                include[i] = false;
                excludedAny = true;
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

        while (await reader.ReadAsync(cancellationToken))
        {
            rowCount++;
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

        return (hasher.GetCurrentHashAsUInt64(), excludedAny, rowCount);
    }

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
            hasher.Append(BitConverter.GetBytes(reader.GetByte(ordinal)));
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
}
