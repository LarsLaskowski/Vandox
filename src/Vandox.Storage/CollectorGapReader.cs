#pragma warning disable RH2003 // Skeleton: removed with the implementation
using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// Reads the gaps of one collector in a range and merges them into spans (record 0227).
/// </summary>
internal static class CollectorGapReader
{
    #region Methods

    /// <summary>
    /// Streams the gap rows and the gap records that concern a collector and overlap the range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="collector">The collector</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the merged spans and whether a kept record names exactly the collector</returns>
    internal static Task<(GapSpanSet Gaps, bool Named)> ReadAsync(SqliteConnection connection, string collector, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003