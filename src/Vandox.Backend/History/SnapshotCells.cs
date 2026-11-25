using System.Globalization;

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
        return LogText.Runs(text, HistoryView.TextChars, HistoryView.TextRuns);
    }

    /// <summary>
    /// Builds the plain runs of a value, formatted with the invariant culture.
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The runs</returns>
    public static IReadOnlyList<TextRun> Plain(IFormattable value)
    {
        return [new TextRun(value.ToString(null, CultureInfo.InvariantCulture), false)];
    }

    /// <summary>
    /// Builds the plain runs of a text.
    /// </summary>
    /// <param name="value">The text</param>
    /// <returns>The runs</returns>
    public static IReadOnlyList<TextRun> Plain(string value)
    {
        return [new TextRun(value, false)];
    }

    /// <summary>
    /// Builds a row.
    /// </summary>
    /// <param name="cells">The cells</param>
    /// <returns>The row</returns>
    public static SnapshotRowView Row(params IReadOnlyList<TextRun>[] cells)
    {
        return new SnapshotRowView
               {
                   Cells = cells
               };
    }

    #endregion // Methods
}