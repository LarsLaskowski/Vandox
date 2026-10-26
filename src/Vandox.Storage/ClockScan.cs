using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The clock bounds the records of a batch give.
/// </summary>
internal sealed class ClockScan
{
    #region Constants

    private const string SelfSource = "agent";

    private const string SendLagName = "agent_send_lag_seconds";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the largest bound of the run-timed records, if any.
    /// </summary>
    internal long? RunBound { get; private set; }

    /// <summary>
    /// Gets the send lag samples that arrived as the agent's highest number, with their bounds and capture times.
    /// </summary>
    internal List<(long Seq, long Bound, DateTimeOffset CapturedAt)> Candidates { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Takes the bounds of one record.
    /// </summary>
    /// <param name="record">The record</param>
    /// <param name="seq">The number</param>
    /// <param name="bound">The bound the record gives</param>
    /// <param name="newest">Whether the record's number is above every stored one</param>
    internal void Add(DataRecord record, long seq, long bound, bool newest)
    {
        if (LiveRules.IsRunTimed(record.Kind))
        {
            RunBound = RunBound is null ? bound : Math.Max(RunBound.Value, bound);
        }

        if (newest && record is { Source: SelfSource, Data: MetricPoint { Name: SendLagName, Value: >= 0 and <= TimelineLimits.MaxCountedSendLagSeconds } })
        {
            Candidates.Add((seq, bound, record.CapturedAt));
        }
    }

    #endregion // Methods
}