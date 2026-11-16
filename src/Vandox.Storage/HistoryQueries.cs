namespace Vandox.Storage;

/// <summary>
/// The statements of the history reads. Every statement is a constant; values are bound as parameters.
/// </summary>
internal static class HistoryQueries
{
    #region Constants

    /// <summary>
    /// The operation named in the error of a history read.
    /// </summary>
    internal const string Context = "store: reading history";

    // Skeleton: the statements below are empty until the implementation; the query plan tests read them.

    /// <summary>
    /// The first source of <c>metrics</c> with its first name.
    /// </summary>
    internal const string MetricsFirst = "";

    /// <summary>
    /// The next name of a source of <c>metrics</c>.
    /// </summary>
    internal const string MetricsNextName = "";

    /// <summary>
    /// The next source of <c>metrics</c>.
    /// </summary>
    internal const string MetricsNextSource = "";

    /// <summary>
    /// The first source of <c>metric_rollups_1h</c> with its first name.
    /// </summary>
    internal const string RollupsFirst = "";

    /// <summary>
    /// The next name of a source of <c>metric_rollups_1h</c>.
    /// </summary>
    internal const string RollupsNextName = "";

    /// <summary>
    /// The next source of <c>metric_rollups_1h</c>.
    /// </summary>
    internal const string RollupsNextSource = "";

    /// <summary>
    /// The stamp read of the side at or before the moment.
    /// </summary>
    internal const string StampAtOrBefore = "";

    /// <summary>
    /// The stamp read of the side after the moment.
    /// </summary>
    internal const string StampAfter = "";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The read of the record of a stamp, built from the record columns.
    /// </summary>
    internal static readonly string SnapshotRecord = string.Empty;

    #endregion // Fields
}