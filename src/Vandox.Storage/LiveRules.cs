using Vandox.Core.Model;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325
/// <summary>
/// Decides whether a record counts as live data.
/// </summary>
public static class LiveRules
{
    #region Methods

    /// <summary>
    /// Gets the allowance of a record kind.
    /// </summary>
    /// <param name="kind">The record kind.</param>
    /// <returns>The allowed distance between the corrected capture time and the receive time.</returns>
    public static TimeSpan AllowanceOf(string kind)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets a value indicating whether a record kind is timed by the agent's run clock.
    /// </summary>
    /// <param name="kind">The record kind.</param>
    /// <returns><see langword="true"/> for every kind except log lines and kernel events.</returns>
    public static bool IsRunTimed(string kind)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets a value indicating whether a record is live.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="receivedAt">The receive time of its batch.</param>
    /// <param name="offsetNs">The agent's clock offset in nanoseconds.</param>
    /// <param name="lowestOpenGapFirstSeq">The first number of the lowest open sequence gap, if any.</param>
    /// <returns><see langword="true"/> when the record is live.</returns>
    public static bool IsLive(DataRecord record, DateTimeOffset receivedAt, long offsetNs, ulong? lowestOpenGapFirstSeq)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}