namespace Vandox.Storage;

/// <summary>
/// The SQL statements of the connection rollups: the fold, the trim and the removal walk. Every statement is a single constant,
/// planned with index searches only.
/// </summary>
internal static class ConnectionRollupSql
{
    #region Constants

    /// <summary>
    /// The <c>meta</c> key of the highest connection snapshot record ID the connection fold has read.
    /// </summary>
    internal const string WatermarkKey = "connection_rollup_after_id";

    /// <summary>
    /// Reads the next connection snapshots of the connection fold.
    /// </summary>
    internal const string ReadSnapshots = "SELECT id, source, captured_at, data FROM records INDEXED BY records_connection_fold WHERE kind = 'connection_snapshot' AND id > $after ORDER BY id LIMIT $limit";

    /// <summary>
    /// Reads the number of programs and addresses a 5-minute connection bucket names.
    /// </summary>
    internal const string ReadCountsFiveMinutes = "SELECT programs, addresses FROM connection_rollup_totals_5m WHERE source = $source AND bucket = $bucket";

    /// <summary>
    /// Looks for the row of a state, program or address in a 5-minute connection bucket.
    /// </summary>
    internal const string HasItemFiveMinutes = "SELECT 1 FROM connection_rollups_5m WHERE source = $source AND bucket = $bucket AND kind = $kind AND name = $name";

    /// <summary>
    /// Merges a state, program or address into the 5-minute connection rollups.
    /// </summary>
    internal const string UpsertItemFiveMinutes = "INSERT INTO connection_rollups_5m" + ItemUpsertTail;

    /// <summary>
    /// Merges the total of a source into the 5-minute connection totals.
    /// </summary>
    internal const string UpsertTotalFiveMinutes = "INSERT INTO connection_rollup_totals_5m" + TotalUpsertTail;

    /// <summary>
    /// Reads the oldest 5-minute connection buckets above the kept address count that ended before a bound.
    /// </summary>
    internal const string ReadTrimFiveMinutes = "SELECT source, bucket, addresses FROM connection_rollup_totals_5m INDEXED BY connection_rollup_totals_5m_trim WHERE addresses > 50 AND bucket <= $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes the addresses of a 5-minute connection bucket outside its top addresses.
    /// </summary>
    internal const string DeleteTrimmedFiveMinutes = "DELETE FROM connection_rollups_5m WHERE source = $source AND bucket = $bucket AND kind = 2 AND name NOT IN (SELECT name FROM connection_rollups_5m WHERE source = $source AND bucket = $bucket AND kind = 2 ORDER BY sum_value DESC, name LIMIT $kept)";

    /// <summary>
    /// Records the trim of a 5-minute connection bucket.
    /// </summary>
    internal const string SetTrimmedFiveMinutes = "UPDATE connection_rollup_totals_5m SET addresses = $addresses, trimmed = trimmed + $trimmed WHERE source = $source AND bucket = $bucket";

    /// <summary>
    /// Reads the oldest 5-minute connection rows below a bound.
    /// </summary>
    internal const string WalkRowsFiveMinutes = "SELECT source, bucket, kind, name FROM connection_rollups_5m WHERE bucket < $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes a 5-minute connection row.
    /// </summary>
    internal const string DeleteRowFiveMinutes = "DELETE FROM connection_rollups_5m WHERE source = $source AND bucket = $bucket AND kind = $kind AND name = $name";

    /// <summary>
    /// Reads the oldest 5-minute connection totals below a bound.
    /// </summary>
    internal const string WalkTotalsFiveMinutes = "SELECT source, bucket FROM connection_rollup_totals_5m WHERE bucket < $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes a 5-minute connection total.
    /// </summary>
    internal const string DeleteTotalFiveMinutes = "DELETE FROM connection_rollup_totals_5m WHERE source = $source AND bucket = $bucket";

    /// <summary>
    /// Reads the number of programs and addresses a hourly connection bucket names.
    /// </summary>
    internal const string ReadCountsHourly = "SELECT programs, addresses FROM connection_rollup_totals_1h WHERE source = $source AND bucket = $bucket";

    /// <summary>
    /// Looks for the row of a state, program or address in a hourly connection bucket.
    /// </summary>
    internal const string HasItemHourly = "SELECT 1 FROM connection_rollups_1h WHERE source = $source AND bucket = $bucket AND kind = $kind AND name = $name";

    /// <summary>
    /// Merges a state, program or address into the hourly connection rollups.
    /// </summary>
    internal const string UpsertItemHourly = "INSERT INTO connection_rollups_1h" + ItemUpsertTail;

    /// <summary>
    /// Merges the total of a source into the hourly connection totals.
    /// </summary>
    internal const string UpsertTotalHourly = "INSERT INTO connection_rollup_totals_1h" + TotalUpsertTail;

    /// <summary>
    /// Reads the oldest hourly connection buckets above the kept address count that ended before a bound.
    /// </summary>
    internal const string ReadTrimHourly = "SELECT source, bucket, addresses FROM connection_rollup_totals_1h INDEXED BY connection_rollup_totals_1h_trim WHERE addresses > 100 AND bucket <= $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes the addresses of a hourly connection bucket outside its top addresses.
    /// </summary>
    internal const string DeleteTrimmedHourly = "DELETE FROM connection_rollups_1h WHERE source = $source AND bucket = $bucket AND kind = 2 AND name NOT IN (SELECT name FROM connection_rollups_1h WHERE source = $source AND bucket = $bucket AND kind = 2 ORDER BY sum_value DESC, name LIMIT $kept)";

    /// <summary>
    /// Records the trim of a hourly connection bucket.
    /// </summary>
    internal const string SetTrimmedHourly = "UPDATE connection_rollup_totals_1h SET addresses = $addresses, trimmed = trimmed + $trimmed WHERE source = $source AND bucket = $bucket";

    /// <summary>
    /// Reads the oldest hourly connection rows below a bound.
    /// </summary>
    internal const string WalkRowsHourly = "SELECT source, bucket, kind, name FROM connection_rollups_1h WHERE bucket < $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes a hourly connection row.
    /// </summary>
    internal const string DeleteRowHourly = "DELETE FROM connection_rollups_1h WHERE source = $source AND bucket = $bucket AND kind = $kind AND name = $name";

    /// <summary>
    /// Reads the oldest hourly connection totals below a bound.
    /// </summary>
    internal const string WalkTotalsHourly = "SELECT source, bucket FROM connection_rollup_totals_1h WHERE bucket < $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes a hourly connection total.
    /// </summary>
    internal const string DeleteTotalHourly = "DELETE FROM connection_rollup_totals_1h WHERE source = $source AND bucket = $bucket";

    /// <summary>
    /// The columns and the merge of a connection row, the same in both tiers: sums clamp to the range of a double.
    /// </summary>
    private const string ItemUpsertTail = "(source, bucket, kind, name, seen, min_value, max_value, sum_value) "
                                          + "VALUES ($source, $bucket, $kind, $name, $seen, $min, $max, $sum) "
                                          + "ON CONFLICT(source, bucket, kind, name) DO UPDATE SET seen = seen + excluded.seen, "
                                          + "min_value = min(min_value, excluded.min_value), max_value = max(max_value, excluded.max_value), "
                                          + "sum_value = MIN(MAX(sum_value + excluded.sum_value, $low), $high)";

    /// <summary>
    /// The columns and the merge of a source total, the same in both tiers: sums clamp to the range of a double.
    /// </summary>
    private const string TotalUpsertTail = "(source, bucket, snapshots, incomplete, capped, programs, addresses, trimmed, process_sum) "
                                           + "VALUES ($source, $bucket, $snapshots, $incomplete, $capped, $programs, $addresses, 0, $processSum) "
                                           + "ON CONFLICT(source, bucket) DO UPDATE SET snapshots = snapshots + excluded.snapshots, incomplete = incomplete + excluded.incomplete, "
                                           + "capped = capped + excluded.capped, programs = excluded.programs, addresses = excluded.addresses, "
                                           + "process_sum = MIN(MAX(process_sum + excluded.process_sum, $low), $high)";

    #endregion // Constants
}