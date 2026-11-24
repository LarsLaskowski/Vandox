using Vandox.Backend.Logs;

namespace Vandox.Backend.History;

/// <summary>
/// Builds the cells and rows of the snapshot tables.
/// </summary>
public static class SnapshotCells
{
    #region Methods

    /// <summary>
    /// Escapes an external text into runs.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The runs</returns>
    public static IReadOnlyList<TextRun> Runs(string text)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds the plain runs of a value, formatted with the invariant culture.
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The runs</returns>
    public static IReadOnlyList<TextRun> Plain(IFormattable value)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds the plain runs of a text.
    /// </summary>
    /// <param name="value">The text</param>
    /// <returns>The runs</returns>
    public static IReadOnlyList<TextRun> Plain(string value)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds a row.
    /// </summary>
    /// <param name="cells">The cells</param>
    /// <returns>The row</returns>
    public static SnapshotRowView Row(params IReadOnlyList<TextRun>[] cells)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // Methods
}