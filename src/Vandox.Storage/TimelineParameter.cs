namespace Vandox.Storage;

/// <summary>
/// The names of the parameters of the timeline statements.
/// </summary>
internal static class TimelineParameter
{
    #region Constants

    /// <summary>
    /// The agent ID.
    /// </summary>
    internal const string Agent = "$agent";

    /// <summary>
    /// The instant a row was changed.
    /// </summary>
    internal const string Changed = "$changed";

    /// <summary>
    /// The state of a gap.
    /// </summary>
    internal const string State = "$state";

    /// <summary>
    /// A capture time.
    /// </summary>
    internal const string Time = "$time";

    /// <summary>
    /// The start of a span.
    /// </summary>
    internal const string From = "$from";

    /// <summary>
    /// The end of a span.
    /// </summary>
    internal const string To = "$to";

    /// <summary>
    /// The first number of a range.
    /// </summary>
    internal const string First = "$first";

    /// <summary>
    /// The last number of a range.
    /// </summary>
    internal const string Last = "$last";

    /// <summary>
    /// The number at the start of a time gap.
    /// </summary>
    internal const string FromSeq = "$fromSeq";

    /// <summary>
    /// The number at the end of a time gap.
    /// </summary>
    internal const string ToSeq = "$toSeq";

    /// <summary>
    /// The ID of a row.
    /// </summary>
    internal const string Id = "$id";

    /// <summary>
    /// A sequence number.
    /// </summary>
    internal const string Seq = "$seq";

    /// <summary>
    /// The lowest number of a sample time.
    /// </summary>
    internal const string Min = "$min";

    /// <summary>
    /// The highest number of a sample time.
    /// </summary>
    internal const string Max = "$max";

    /// <summary>
    /// The sort of a clock sample.
    /// </summary>
    internal const string Sort = "$sort";

    /// <summary>
    /// The cause of a gap.
    /// </summary>
    internal const string Cause = "$cause";

    /// <summary>
    /// The instant a gap was detected.
    /// </summary>
    internal const string Detected = "$detected";

    #endregion // Constants
}