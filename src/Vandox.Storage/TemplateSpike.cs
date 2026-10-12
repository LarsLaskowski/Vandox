namespace Vandox.Storage;

/// <summary>
/// An hour in which a template has more lines than its usual level.
/// </summary>
public sealed class TemplateSpike
{
    #region Properties

    /// <summary>
    /// Gets or sets the template.
    /// </summary>
    public LogTemplate Template { get; set; } = new();

    /// <summary>
    /// Gets or sets the start of the hour, in UTC.
    /// </summary>
    public DateTimeOffset Hour { get; set; }

    /// <summary>
    /// Gets or sets the number of lines in the hour.
    /// </summary>
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the usual level: lines per covered hour of the baseline.
    /// </summary>
    public double UsualLevel { get; set; }

    /// <summary>
    /// Gets or sets the number of baseline hours in which the source has lines.
    /// </summary>
    public int CoveredHours { get; set; }

    #endregion // Properties
}