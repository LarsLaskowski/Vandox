using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// The IDs of the non-imported log lines captured within the last two minutes of the lines read, so that the origin of a kill is judged per kill without remembering every line.
/// </summary>
#pragma warning disable RH2003, S2325 // Skeleton: removed with the implementation
internal sealed class RecentAgentLines
{
    #region Properties

    /// <summary>
    /// Gets the number of lines held.
    /// </summary>
    internal int Count => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a line; only the ID of a line that is not imported is kept.
    /// </summary>
    /// <param name="record">The stored line</param>
    internal void Add(StoredRecord record)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Forgets the lines captured before a point in time.
    /// </summary>
    /// <param name="capturedAt">The point in time; lines captured earlier than two minutes before it are forgotten</param>
    internal void ForgetBefore(DateTimeOffset capturedAt)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Determines whether one of the record IDs belongs to a held line.
    /// </summary>
    /// <param name="recordIds">The record IDs of a kill</param>
    /// <returns><c>true</c> when one of the IDs is held</returns>
    internal bool ContainsAny(IReadOnlyList<long> recordIds)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003, S2325 // Skeleton: removed with the implementation