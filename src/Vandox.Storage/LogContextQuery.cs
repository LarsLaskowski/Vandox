namespace Vandox.Storage;

/// <summary>
/// The query for the neighbours of one log line.
/// </summary>
public sealed class LogContextQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the log line.
    /// </summary>
    public long RecordId { get; set; }

    /// <summary>
    /// Gets or sets the number of lines before it.
    /// </summary>
    public int Before { get; set; }

    /// <summary>
    /// Gets or sets the number of lines after it.
    /// </summary>
    public int After { get; set; }

    #endregion // Properties
}