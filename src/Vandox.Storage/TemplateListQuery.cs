namespace Vandox.Storage;

/// <summary>
/// The query for the templates of a range.
/// </summary>
public sealed class TemplateListQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the exact source; empty for any.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the exact program; empty for any.
    /// </summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the largest number of templates.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}