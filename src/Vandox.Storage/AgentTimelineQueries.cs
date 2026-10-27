using Microsoft.Data.Sqlite;

using Vandox.Core.Wire;

namespace Vandox.Storage;

/// <summary>
/// The statements of the agent timeline and the validation of its queries. Every statement finds its rows by an index search: the greatest key at or below
/// a value, then a check of the end, or the lowest key, so the work per stored record does not grow with the number of open gaps.
/// </summary>
internal static class AgentTimelineQueries
{
    #region Constants

    /// <summary>
    /// Reads the stream state of an agent.
    /// </summary>
    internal const string LoadStream = "SELECT floor_seq, max_seq, max_captured_at, fill_at, open_gaps, offset_ns, measured_at, clock_off, last_bound_ns, last_captured_at, last_received_at FROM agent_streams WHERE agent_id = $agent";

    /// <summary>
    /// Writes the stream state of an agent.
    /// </summary>
    internal const string SaveStream = "INSERT INTO agent_streams(agent_id, floor_seq, max_seq, max_captured_at, fill_at, open_gaps, offset_ns, measured_at, clock_off, last_bound_ns, last_captured_at, last_received_at) "
                                       + "VALUES ($agent, $floor, $max, $captured, $fill, $open, $offset, $measured, $off, $lastBound, $lastCaptured, $lastReceived) "
                                       + "ON CONFLICT(agent_id) DO UPDATE SET floor_seq = excluded.floor_seq, max_seq = excluded.max_seq, max_captured_at = excluded.max_captured_at, "
                                       + "fill_at = excluded.fill_at, open_gaps = excluded.open_gaps, offset_ns = excluded.offset_ns, measured_at = excluded.measured_at, clock_off = excluded.clock_off, "
                                       + "last_bound_ns = excluded.last_bound_ns, last_captured_at = excluded.last_captured_at, last_received_at = excluded.last_received_at";

    /// <summary>
    /// Finds the sequence gap that is not closed with the greatest first number at or below a number.
    /// </summary>
    internal const string FindGap = "SELECT id, first_seq, last_seq, state, cause, from_at, to_at, detected_at FROM gaps "
                                    + "WHERE agent_id = $agent AND kind = 'sequence' AND state <> 'closed' AND first_seq <= $seq ORDER BY first_seq DESC LIMIT 1";

    /// <summary>
    /// Finds the open sequence gap with the greatest first number at or below a number.
    /// </summary>
    internal const string FindOpenGap = "SELECT id, first_seq, last_seq, state, cause, from_at, to_at, detected_at FROM gaps "
                                        + "WHERE agent_id = $agent AND kind = 'sequence' AND state = 'open' AND first_seq <= $seq ORDER BY first_seq DESC LIMIT 1";

    /// <summary>
    /// Finds the first number of the lowest open sequence gap.
    /// </summary>
    internal const string LowestOpenGap = "SELECT first_seq FROM gaps WHERE agent_id = $agent AND kind = 'sequence' AND state = 'open' ORDER BY first_seq LIMIT 1";

    /// <summary>
    /// Inserts a gap.
    /// </summary>
    internal const string InsertGap = "INSERT INTO gaps(agent_id, kind, cause, state, first_seq, last_seq, from_seq, to_seq, from_at, to_at, detected_at, changed_at) "
                                      + "VALUES ($agent, $kind, $cause, $state, $first, $last, $fromSeq, $toSeq, $from, $to, $detected, $changed) ON CONFLICT DO NOTHING";

    /// <summary>
    /// Changes the number range, the state and the cause of a sequence gap.
    /// </summary>
    internal const string UpdateSequenceGap = "UPDATE gaps SET first_seq = $first, last_seq = $last, state = $state, cause = $cause, changed_at = $changed WHERE id = $id";

    /// <summary>
    /// Makes every open sequence gap of an agent final.
    /// </summary>
    internal const string FinalizeOpenGaps = "UPDATE gaps SET state = 'final', changed_at = $changed WHERE agent_id = $agent AND kind = 'sequence' AND state = 'open'";

    /// <summary>
    /// Finds the time gap that is not closed with the greatest start before a time.
    /// </summary>
    internal const string FindTimeGap = "SELECT id, from_at, to_at, from_seq, to_seq, cause, detected_at FROM gaps "
                                        + "WHERE agent_id = $agent AND kind = 'time' AND state <> 'closed' AND from_at < $time ORDER BY from_at DESC LIMIT 1";

    /// <summary>
    /// Changes the span, the numbers and the state of a time gap.
    /// </summary>
    internal const string UpdateTimeGap = "UPDATE gaps SET from_at = $from, to_at = $to, from_seq = $fromSeq, to_seq = $toSeq, state = $state, changed_at = $changed WHERE id = $id";

    /// <summary>
    /// Inserts a sample time unless it exists.
    /// </summary>
    internal const string InsertSampleTime = "INSERT INTO agent_sample_times(agent_id, captured_at, min_seq, max_seq) VALUES ($agent, $time, $min, $max) ON CONFLICT DO NOTHING";

    /// <summary>
    /// Widens the numbers of an existing sample time.
    /// </summary>
    internal const string WidenSampleTime = "UPDATE agent_sample_times SET min_seq = min(min_seq, $min), max_seq = max(max_seq, $max) WHERE agent_id = $agent AND captured_at = $time";

    /// <summary>
    /// Finds the sample time before a time.
    /// </summary>
    internal const string PreviousSampleTime = "SELECT captured_at, max_seq FROM agent_sample_times WHERE agent_id = $agent AND captured_at < $time ORDER BY captured_at DESC LIMIT 1";

    /// <summary>
    /// Finds the sample time after a time.
    /// </summary>
    internal const string NextSampleTime = "SELECT captured_at, min_seq FROM agent_sample_times WHERE agent_id = $agent AND captured_at > $time ORDER BY captured_at LIMIT 1";

    /// <summary>
    /// Writes the largest bound of a bucket.
    /// </summary>
    internal const string UpsertClockSample = "INSERT INTO agent_clock_samples(agent_id, sort, bucket, bound_ns) VALUES ($agent, $sort, $bucket, $bound) "
                                              + "ON CONFLICT(agent_id, sort, bucket) DO UPDATE SET bound_ns = max(bound_ns, excluded.bound_ns)";

    /// <summary>
    /// Deletes the clock sample rows beyond the newest ones.
    /// </summary>
    internal const string PruneClockSamples = "DELETE FROM agent_clock_samples WHERE agent_id = $agent AND sort = $sort "
                                              + "AND bucket < (SELECT bucket FROM agent_clock_samples WHERE agent_id = $agent AND sort = $sort ORDER BY bucket DESC LIMIT 1 OFFSET $keep)";

    /// <summary>
    /// Reads the clock sample rows of a sort, oldest first.
    /// </summary>
    internal const string ReadClockSamples = "SELECT bucket, bound_ns FROM agent_clock_samples WHERE agent_id = $agent AND sort = $sort ORDER BY bucket";

    /// <summary>
    /// Reads the measured clock offset of an agent.
    /// </summary>
    internal const string ReadClock = "SELECT offset_ns, measured_at FROM agent_streams WHERE agent_id = $agent";

    /// <summary>
    /// Reads a page of the gaps of an agent of any state.
    /// </summary>
    internal const string ReadGaps = GapReadSql.Columns + " ORDER BY gaps.id LIMIT $limit";

    /// <summary>
    /// Reads a page of the gaps of an agent in one state.
    /// </summary>
    internal const string ReadGapsInState = GapReadSql.Columns + " AND " + GapReadSql.State + " = $state ORDER BY gaps.id LIMIT $limit";

    private const int MaxPage = 1000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks a gap query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query violates a rule</exception>
    internal static void Validate(GapQuery query)
    {
        ValidateAgentId(query.AgentId);
        RecordQueries.Require(query.State.Length == 0 || query.State == GapState.Open || query.State == GapState.Closed || query.State == GapState.Final, "unknown gap state");
        RecordQueries.Require(query.AfterId >= 0, "after id must not be negative");
        RecordQueries.Require(query.Limit is >= 1 and <= MaxPage, "limit must be 1 to 1000");
    }

    /// <summary>
    /// Checks an agent ID.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <exception cref="InvalidQueryException">The ID breaks the wire rule</exception>
    internal static void ValidateAgentId(string agentId)
    {
        RecordQueries.Require(WireFormat.ValidateAgentId(agentId) is null, "agent id is invalid");
    }

    /// <summary>
    /// Maps the current row of <see cref="ReadGaps"/> or <see cref="ReadGapsInState"/>.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <param name="reader">The reader</param>
    /// <returns>The gap</returns>
    internal static AgentGap ReadGap(string agentId, SqliteDataReader reader)
    {
        return new AgentGap
               {
                   Id = reader.GetInt64(0),
                   AgentId = agentId,
                   Kind = reader.GetString(1),
                   Cause = reader.GetString(2),
                   State = reader.GetString(3),
                   FirstSeq = reader.IsDBNull(4) ? null : (ulong)reader.GetInt64(4),
                   LastSeq = reader.IsDBNull(5) ? null : (ulong)reader.GetInt64(5),
                   From = StorageTime.FromNanoseconds(reader.GetInt64(6)),
                   To = StorageTime.FromNanoseconds(reader.GetInt64(7)),
                   DetectedAt = StorageTime.FromNanoseconds(reader.GetInt64(8)),
                   ChangedAt = StorageTime.FromNanoseconds(reader.GetInt64(9))
               };
    }

    #endregion // Methods
}