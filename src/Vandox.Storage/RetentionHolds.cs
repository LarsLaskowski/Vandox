namespace Vandox.Storage;

/// <summary>
/// Stores the holds of the kill scan: the windows around the OOM kills whose data the removal keeps.
/// </summary>
internal static class RetentionHolds
{
    #region Methods

    /// <summary>
    /// Checks the holds and the progress of a kill scan.
    /// </summary>
    /// <param name="holds">The new holds</param>
    /// <param name="progress">The progress</param>
    /// <exception cref="InvalidQueryException">A hold or the progress breaks a rule</exception>
    internal static void Validate(IReadOnlyList<RetentionHold> holds, KillScanProgress progress)
    {
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(progress);
        RecordQueries.Require(progress.ScannedId >= 0, "scanned ID must not be negative");

        foreach (var hold in holds)
        {
            RecordQueries.Require(StorageTime.InStorableRange(hold.Start) && StorageTime.InStorableRange(hold.End), "hold must be within the storable range");
            RecordQueries.Require(hold.Start < hold.End, "hold must start before it ends");
        }
    }

    /// <summary>
    /// Merges the new holds with the stored holds they overlap, so stored holds never overlap, and stores the scan progress.
    /// </summary>
    /// <param name="session">The session of the write</param>
    /// <param name="holds">The new holds</param>
    /// <param name="progress">The progress</param>
    /// <param name="cancellationToken">Cancels the write between holds</param>
    internal static void Save(RetentionSession session, IReadOnlyList<RetentionHold> holds, KillScanProgress progress, CancellationToken cancellationToken)
    {
        foreach (var hold in holds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = StorageTime.ToNanoseconds(hold.Start);
            var end = StorageTime.ToNanoseconds(hold.End);
            var overlapping = new List<long>();

            using (var reader = session.Statement(RetentionQueries.OverlappingHolds).Set("$end", end).Query())
            {
                while (reader.Read() && reader.GetInt64(2) >= start)
                {
                    overlapping.Add(reader.GetInt64(0));
                    start = Math.Min(start, reader.GetInt64(1));
                    end = Math.Max(end, reader.GetInt64(2));
                }
            }

            foreach (var id in overlapping)
            {
                session.Statement(RetentionQueries.DeleteHold).Set("$id", id).Execute();
            }

            session.Statement(RetentionQueries.InsertHold).Set("$start", start).Set("$end", end).Execute();
        }

        session.WriteNumber(RetentionQueries.KillScanKey, progress.ScannedId);
    }

    #endregion // Methods
}