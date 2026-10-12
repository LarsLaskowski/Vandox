namespace Vandox.Storage;

/// <summary>
/// A log template of a source and program with its statistics.
/// </summary>
public sealed class LogTemplate
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the template.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program.
    /// </summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the template text.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the capture time of the earliest line, in UTC.
    /// </summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>
    /// Gets or sets the capture time of the latest line, in UTC.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// Gets or sets the number of lines.
    /// </summary>
    public long Count { get; set; }

    #endregion // Properties
}