namespace Vandox.Backend.Reports;

/// <summary>
/// One block of the report: a heading, a paragraph, a list item or a table.
/// </summary>
public sealed class ReportBlock
{
    #region Properties

    /// <summary>
    /// Gets the kind of the block.
    /// </summary>
    public ReportBlockKind Kind { get; init; }

    /// <summary>
    /// Gets the level: 1 to 3 for a heading, 0 or 1 for an item, otherwise 0.
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// Gets the pieces of a heading, a paragraph or an item.
    /// </summary>
    public IReadOnlyList<ReportPiece> Pieces { get; init; } = [];

    /// <summary>
    /// Gets, per column of a table, whether it is right-aligned.
    /// </summary>
    public IReadOnlyList<bool> AlignRight { get; init; } = [];

    /// <summary>
    /// Gets the rows of a table, the header row first.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];

    #endregion // Properties
}