namespace Vandox.Storage;

/// <summary>
/// Parts of the statements that read gaps.
/// </summary>
internal static class GapReadSql
{
    #region Constants

    /// <summary>
    /// The state of a gap as it is read: a time gap that is stored open reads final when no open sequence gap lies between its neighbouring records.
    /// </summary>
    internal const string State = "CASE WHEN gaps.kind = 'time' AND gaps.state = 'open' AND NOT COALESCE((SELECT s.last_seq FROM gaps AS s WHERE s.agent_id = gaps.agent_id "
                                  + "AND s.kind = 'sequence' AND s.state = 'open' AND s.first_seq < gaps.to_seq ORDER BY s.first_seq DESC LIMIT 1) > gaps.from_seq, 0) THEN 'final' ELSE gaps.state END";

    /// <summary>
    /// The columns and the filter shared by both read statements.
    /// </summary>
    internal const string Columns = "SELECT gaps.id, gaps.kind, gaps.cause, " + State + ", gaps.first_seq, gaps.last_seq, gaps.from_at, gaps.to_at, gaps.detected_at, gaps.changed_at FROM gaps "
                                    + "WHERE gaps.agent_id = $agent AND gaps.id > $after";

    #endregion // Constants
}