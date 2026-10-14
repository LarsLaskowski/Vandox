namespace Vandox.Storage;

/// <summary>
/// A template with its number of lines in a range.
/// </summary>
public sealed class TemplateCount
{
    #region Properties

    /// <summary>
    /// Gets or sets the template.
    /// </summary>
    public LogTemplate Template { get; set; } = new();

    /// <summary>
    /// Gets or sets the number of its lines in the hours of the range.
    /// </summary>
    public long CountInRange { get; set; }

    #endregion // Properties
}