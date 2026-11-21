using Vandox.Backend.History;

namespace Vandox.Backend.Processes;

#pragma warning disable RH2003 // Skeleton: removed with the implementation
/// <summary>
/// The comparison of the programs of one source at two points.
/// </summary>
public sealed class ProgramComparisonView
{
    #region Properties

    /// <summary>
    /// Gets the text that replaces the table, for example when a point has no snapshot; empty when the table is shown.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets the heading: both points and the source.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the closing line.
    /// </summary>
    public string Closing { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note on an incomplete snapshot; empty when there is none.
    /// </summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table.
    /// </summary>
    public SnapshotTableView Table { get; init; } = new();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Compares the programs of two snapshots.
    /// </summary>
    /// <param name="earlier">The earlier snapshot</param>
    /// <param name="later">The later snapshot</param>
    /// <returns>The comparison</returns>
    public static ProgramComparisonView Compare(ProgramFigures earlier, ProgramFigures later)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives a comparison that only says why there is none.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The comparison</returns>
    public static ProgramComparisonView Missing(string text)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003 // Skeleton: removed with the implementation