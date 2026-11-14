namespace Vandox.Storage;

/// <summary>
/// The SQL statements of the retention job: the fold, the kill scan, the removal steps and the holds. Every statement is a
/// single constant, planned with index searches only.
/// </summary>
internal static class RetentionQueries
{
    #region Constants

    /// <summary>
    /// The <c>meta</c> key of the highest metric record ID the fold has read.
    /// </summary>
    internal const string RollupWatermarkKey = "rollup_after_id";

    /// <summary>
    /// The <c>meta</c> key of the raw horizon below which sample times are pruned.
    /// </summary>
    internal const string SampleFloorKey = "sample_time_floor";

    /// <summary>
    /// The <c>meta</c> key of the highest record ID the kill scan has read.
    /// </summary>
    internal const string KillScanKey = "kill_scan_after_id";

    /// <summary>
    /// Reads a <c>meta</c> value.
    /// </summary>
    internal const string ReadMeta = "SELECT value FROM meta WHERE key = $key";

    /// <summary>
    /// Writes a <c>meta</c> value.
    /// </summary>
    internal const string WriteMeta = "INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";

    /// <summary>
    /// Reads the highest record ID.
    /// </summary>
    internal const string HighestId = "SELECT coalesce(max(id), 0) FROM records";

    /// <summary>
    /// Reads the next metric values of the fold.
    /// </summary>
    internal const string ReadMetrics = "SELECT record_id, source, name, labels, captured_at, value FROM metrics WHERE record_id > $after ORDER BY record_id LIMIT $limit";

    /// <summary>
    /// Merges a bucket into the 5-minute rollups.
    /// </summary>
    internal const string UpsertFiveMinutes = "INSERT INTO metric_rollups_5m(source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id) "
                                              + "VALUES ($source, $name, $labels, $bucket, $min, $max, $sum, $count, $last, $lastAt, $lastId) "
                                              + "ON CONFLICT(source, name, labels, bucket) DO UPDATE SET min_value = min(min_value, excluded.min_value), max_value = max(max_value, excluded.max_value), "
                                              + "sum_value = MIN(MAX(sum_value + excluded.sum_value, $low), $high), count = count + excluded.count, "
                                              + "last_value = CASE WHEN excluded.last_at > last_at OR (excluded.last_at = last_at AND excluded.last_record_id > last_record_id) THEN excluded.last_value ELSE last_value END, "
                                              + "last_record_id = CASE WHEN excluded.last_at > last_at OR (excluded.last_at = last_at AND excluded.last_record_id > last_record_id) THEN excluded.last_record_id ELSE last_record_id END, "
                                              + "last_at = max(last_at, excluded.last_at)";

    /// <summary>
    /// Merges a bucket into the hourly rollups.
    /// </summary>
    internal const string UpsertHourly = "INSERT INTO metric_rollups_1h(source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at, last_record_id) "
                                         + "VALUES ($source, $name, $labels, $bucket, $min, $max, $sum, $count, $last, $lastAt, $lastId) "
                                         + "ON CONFLICT(source, name, labels, bucket) DO UPDATE SET min_value = min(min_value, excluded.min_value), max_value = max(max_value, excluded.max_value), "
                                         + "sum_value = MIN(MAX(sum_value + excluded.sum_value, $low), $high), count = count + excluded.count, "
                                         + "last_value = CASE WHEN excluded.last_at > last_at OR (excluded.last_at = last_at AND excluded.last_record_id > last_record_id) THEN excluded.last_value ELSE last_value END, "
                                         + "last_record_id = CASE WHEN excluded.last_at > last_at OR (excluded.last_at = last_at AND excluded.last_record_id > last_record_id) THEN excluded.last_record_id ELSE last_record_id END, "
                                         + "last_at = max(last_at, excluded.last_at)";

    /// <summary>
    /// Reads the 5-minute buckets of one series.
    /// </summary>
    internal const string ReadFiveMinutes = "SELECT source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at FROM metric_rollups_5m "
                                            + "WHERE source = $source AND name = $name AND bucket >= $from AND bucket < $to ORDER BY labels, bucket LIMIT $limit";

    /// <summary>
    /// Reads the hourly buckets of one series.
    /// </summary>
    internal const string ReadHourly = "SELECT source, name, labels, bucket, min_value, max_value, sum_value, count, last_value, last_at FROM metric_rollups_1h "
                                       + "WHERE source = $source AND name = $name AND bucket >= $from AND bucket < $to ORDER BY labels, bucket LIMIT $limit";

    /// <summary>
    /// Reads the kernel events above a record ID, for the kill scan.
    /// </summary>
    internal const string ScanKernelEvents = "SELECT r.id, r.kind, r.origin, r.source, r.agent_id, r.seq, r.captured_at, r.received_at, r.boot_id, r.clock_offset_ns, r.data, "
                                             + "NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, r.live FROM records AS r WHERE +r.kind = 'kernel_event' AND r.id > $after ORDER BY r.id LIMIT $limit";

    /// <summary>
    /// Reads the holds that overlap a range.
    /// </summary>
    internal const string ReadHolds = "SELECT started_at, ended_at FROM retention_holds WHERE started_at <= $end AND ended_at >= $start ORDER BY started_at";

    /// <summary>
    /// Reads the stored holds that overlap a new hold, the one with the greatest start first.
    /// </summary>
    internal const string OverlappingHolds = "SELECT id, started_at, ended_at FROM retention_holds WHERE started_at <= $end ORDER BY started_at DESC";

    /// <summary>
    /// Deletes a hold.
    /// </summary>
    internal const string DeleteHold = "DELETE FROM retention_holds WHERE id = $id";

    /// <summary>
    /// Stores a hold.
    /// </summary>
    internal const string InsertHold = "INSERT INTO retention_holds(started_at, ended_at) VALUES ($start, $end)";

    /// <summary>
    /// Reads the hold with the greatest start at or below a position.
    /// </summary>
    internal const string HoldAt = "SELECT started_at, ended_at FROM retention_holds WHERE started_at <= $position ORDER BY started_at DESC LIMIT 1";

    /// <summary>
    /// Reads the start of the first hold above a position.
    /// </summary>
    internal const string NextHoldStart = "SELECT started_at FROM retention_holds WHERE started_at > $position ORDER BY started_at LIMIT 1";

    /// <summary>
    /// Walks the removable records of one kind in capture order from a position up to a bound.
    /// </summary>
    internal const string WalkRecords = "SELECT id, captured_at FROM records WHERE kind = $kind AND origin <> 'import' AND (captured_at, id) > ($at, $id) AND captured_at < $upper "
                                        + "ORDER BY captured_at, id LIMIT $limit";

    /// <summary>
    /// Tells whether a connection snapshot is the newest mark of its agent.
    /// </summary>
    internal const string IsNewestMark = "SELECT 1 FROM connection_marks WHERE record_id = $id AND NOT EXISTS "
                                         + "(SELECT 1 FROM connection_marks AS later WHERE later.agent_id = connection_marks.agent_id AND later.seq > connection_marks.seq)";

    /// <summary>
    /// Tells whether a connection snapshot has a listener finding.
    /// </summary>
    internal const string HasFinding = "SELECT 1 FROM connection_marks WHERE record_id = $id AND (new_count > 0 OR changed_count > 0) "
                                       + "UNION ALL SELECT 1 FROM listener_comparisons WHERE record_id = $id AND finding = 1 LIMIT 1";

    /// <summary>
    /// Removes the full-text entry of a log line.
    /// </summary>
    internal const string DeleteFullText = "INSERT INTO log_fts(log_fts, rowid, message) SELECT 'delete', record_id, message FROM log_lines WHERE record_id = $id";

    /// <summary>
    /// Merges a bounded number of pages of the full-text index towards one segment (a negative page count), which lets the entries of removed lines go.
    /// </summary>
    internal const string MergeFullText = "INSERT INTO log_fts(log_fts, rank) VALUES ('merge', $pages)";

    /// <summary>
    /// Deletes a log line.
    /// </summary>
    internal const string DeleteLogLine = "DELETE FROM log_lines WHERE record_id = $id";

    /// <summary>
    /// Deletes a metric value.
    /// </summary>
    internal const string DeleteMetric = "DELETE FROM metrics WHERE record_id = $id";

    /// <summary>
    /// Deletes the connection mark of a snapshot.
    /// </summary>
    internal const string DeleteMark = "DELETE FROM connection_marks WHERE record_id = $id";

    /// <summary>
    /// Deletes the listener comparison of a snapshot.
    /// </summary>
    internal const string DeleteComparison = "DELETE FROM listener_comparisons WHERE record_id = $id";

    /// <summary>
    /// Deletes a record.
    /// </summary>
    internal const string DeleteRecord = "DELETE FROM records WHERE id = $id";

    /// <summary>
    /// Reads the first agent after an agent ID.
    /// </summary>
    internal const string NextAgent = "SELECT agent_id FROM agent_streams WHERE agent_id > $after ORDER BY agent_id LIMIT 1";

    /// <summary>
    /// Reads the newest sample time of an agent.
    /// </summary>
    internal const string NewestSampleTime = "SELECT max(captured_at) FROM agent_sample_times WHERE agent_id = $agent";

    /// <summary>
    /// Reads the oldest sample times of an agent below a bound.
    /// </summary>
    internal const string OldSampleTimes = "SELECT captured_at FROM agent_sample_times WHERE agent_id = $agent AND captured_at < $upper ORDER BY captured_at LIMIT $limit";

    /// <summary>
    /// Deletes a sample time.
    /// </summary>
    internal const string DeleteSampleTime = "DELETE FROM agent_sample_times WHERE agent_id = $agent AND captured_at = $at";

    /// <summary>
    /// Walks the gap rows by the end of their span.
    /// </summary>
    internal const string WalkGaps = "SELECT gaps.id, gaps.kind, " + GapReadSql.State + ", gaps.from_at, gaps.to_at FROM gaps WHERE gaps.to_at < $upper AND (gaps.to_at, gaps.id) > ($at, $id) "
                                     + "ORDER BY gaps.to_at, gaps.id LIMIT $limit";

    /// <summary>
    /// Deletes a gap row.
    /// </summary>
    internal const string DeleteGap = "DELETE FROM gaps WHERE id = $id";

    /// <summary>
    /// Reads the oldest 5-minute buckets below a bound.
    /// </summary>
    internal const string WalkFiveMinutes = "SELECT source, name, labels, bucket FROM metric_rollups_5m WHERE bucket < $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes a 5-minute bucket.
    /// </summary>
    internal const string DeleteFiveMinutes = "DELETE FROM metric_rollups_5m WHERE source = $source AND name = $name AND labels = $labels AND bucket = $bucket";

    /// <summary>
    /// Reads the oldest hourly buckets below a bound.
    /// </summary>
    internal const string WalkHourly = "SELECT source, name, labels, bucket FROM metric_rollups_1h WHERE bucket < $upper ORDER BY bucket LIMIT $limit";

    /// <summary>
    /// Deletes an hourly bucket.
    /// </summary>
    internal const string DeleteHourly = "DELETE FROM metric_rollups_1h WHERE source = $source AND name = $name AND labels = $labels AND bucket = $bucket";

    /// <summary>
    /// Walks the template hours below a bound in hour order.
    /// </summary>
    internal const string WalkTemplateHours = "SELECT h.template_id, h.hour, t.source FROM log_template_hours AS h JOIN log_templates AS t ON t.id = h.template_id "
                                              + "WHERE h.hour < $upper AND (h.hour, h.template_id) > ($at, $id) ORDER BY h.hour, h.template_id LIMIT $limit";

    /// <summary>
    /// Tells whether a stored log line of a source lies in a time range.
    /// </summary>
    internal const string HasLogLine = "SELECT 1 FROM records WHERE kind = 'log_line' AND source = $source AND captured_at >= $from AND captured_at < $to LIMIT 1";

    /// <summary>
    /// Deletes a template hour.
    /// </summary>
    internal const string DeleteTemplateHour = "DELETE FROM log_template_hours WHERE template_id = $id AND hour = $hour";

    /// <summary>
    /// Deletes a source hour.
    /// </summary>
    internal const string DeleteSourceHour = "DELETE FROM log_source_hours WHERE source = $source AND hour = $hour";

    /// <summary>
    /// Deletes a template that has no hour left.
    /// </summary>
    internal const string DeleteEmptyTemplate = "DELETE FROM log_templates WHERE id = $id AND NOT EXISTS (SELECT 1 FROM log_template_hours WHERE template_id = $id)";

    /// <summary>
    /// Walks the incidents without a note that ended before a bound.
    /// </summary>
    internal const string WalkIncidents = "SELECT id, started_at FROM incidents WHERE started_at < $upper AND ended_at < $upper AND (started_at, id) > ($at, $id) "
                                          + "AND NOT EXISTS (SELECT 1 FROM incident_notes WHERE incident_notes.incident_id = incidents.id) ORDER BY started_at, id LIMIT $limit";

    /// <summary>
    /// Deletes an incident.
    /// </summary>
    internal const string DeleteIncident = "DELETE FROM incidents WHERE id = $id";

    /// <summary>
    /// Walks the holds that ended before a bound.
    /// </summary>
    internal const string WalkHolds = "SELECT id, started_at, ended_at FROM retention_holds WHERE started_at < $upper AND ended_at < $upper AND (started_at, id) > ($at, $id) ORDER BY started_at, id LIMIT $limit";

    /// <summary>
    /// Tells whether a stored incident with a note overlaps a range.
    /// </summary>
    internal const string HasNotedIncident = "SELECT 1 FROM incidents WHERE started_at <= $end AND ended_at >= $start "
                                             + "AND EXISTS (SELECT 1 FROM incident_notes WHERE incident_notes.incident_id = incidents.id) LIMIT 1";

    #endregion // Constants
}