namespace Vandox.Backend.Reports;

/// <summary>
/// The kind of a block of the report.
/// </summary>
public enum ReportBlockKind
{
    /// <summary>
    /// A heading of level 1 to 3.
    /// </summary>
    Heading,

    /// <summary>
    /// A paragraph.
    /// </summary>
    Paragraph,

    /// <summary>
    /// A list item of level 0 or 1.
    /// </summary>
    Item,

    /// <summary>
    /// A table, the header row first.
    /// </summary>
    Table
}