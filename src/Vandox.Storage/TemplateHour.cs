namespace Vandox.Storage;

/// <summary>
/// The number of lines of a template in one UTC hour.
/// </summary>
public sealed class TemplateHour
{
    #region Properties

    /// <summary>
    /// Gets or sets the start of the hour, in UTC.
    /// </summary>
    public DateTimeOffset Hour { get; set; }

    /// <summary>
    /// Gets or sets the number of lines.
    /// </summary>
    public long Count { get; set; }

    #endregion // Properties
}