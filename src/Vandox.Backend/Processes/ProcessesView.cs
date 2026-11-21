namespace Vandox.Backend.Processes;

/// <summary>
/// The constants and texts of the processes view.
/// </summary>
public static class ProcessesView
{
    #region Constants

    /// <summary>
    /// The number of programs each chart shows.
    /// </summary>
    public const int Programs = 10;

    /// <summary>
    /// The name of the line of all programs not shown on their own.
    /// </summary>
    public const string OtherPrograms = "Other programs";

    /// <summary>
    /// The text for a range without process data.
    /// </summary>
    public const string NoDataText = "No process data is stored in this range.";

    /// <summary>
    /// The text for a range that starts before the oldest stored process data of the source.
    /// </summary>
    public const string StartsBeforeBucketsText = "This range starts before the oldest stored process data of this source.";

    /// <summary>
    /// The text for steps folded from a snapshot that does not list every program.
    /// </summary>
    public const string IncompleteSnapshotsText = "Some snapshots in this range do not list every program.";

    /// <summary>
    /// The format of the note on snapshots not folded yet; the argument is the time.
    /// </summary>
    public const string NotFoldedFormat = "Values after {0} UTC are not complete yet and may still change.";

    /// <summary>
    /// The text after a cut source list.
    /// </summary>
    public const string SourceListCutText = "The list shows the first 256 sources.";

    /// <summary>
    /// The note on a memory chart measured by resident memory.
    /// </summary>
    public const string RssNote = "RSS counts shared memory in every program that maps it, so the stack can exceed the server's memory.";

    /// <summary>
    /// The error of a second point without a moment.
    /// </summary>
    public const string CompareNeedsMomentText = "The address needs both a moment and a second point for a comparison.";

    /// <summary>
    /// The text while no source has process data in the range.
    /// </summary>
    public const string NoSourceText = "No source has process data in this range, so no snapshot is shown.";

    /// <summary>
    /// The text when the source could not be read.
    /// </summary>
    public const string SourceFailureText = "The source could not be read, so no snapshot is shown.";

    /// <summary>
    /// The text of a read that took longer than the deadline.
    /// </summary>
    public const string ChartTimeoutText = "Reading the processes took longer than 10 seconds and was stopped. Narrow the range.";

    /// <summary>
    /// The text of a read that failed.
    /// </summary>
    public const string ChartFailureText = "The processes could not be read. The service log has the details.";

    /// <summary>
    /// The text for a first point without a snapshot.
    /// </summary>
    public const string NoSnapshotFirstPointText = "No process snapshot was stored within 10 minutes of the first point.";

    /// <summary>
    /// The text for a second point without a snapshot.
    /// </summary>
    public const string NoSnapshotSecondPointText = "No process snapshot was stored within 10 minutes of the second point.";

    /// <summary>
    /// The text for two points that show one snapshot.
    /// </summary>
    public const string SameSnapshotText = "Both points show the same snapshot; choose points further apart.";

    /// <summary>
    /// The note on a comparison with a snapshot that does not list every program.
    /// </summary>
    public const string IncompleteComparisonNote = "One of the two snapshots does not list every program, so no program is marked new or gone.";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Gives the link to the processes view for an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The link</returns>
    public static string ProcessesHref(ProcessesAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return "processes" + address.ToQuery();
    }

    #endregion // Methods
}