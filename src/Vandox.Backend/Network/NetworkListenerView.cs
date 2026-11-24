using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// The panel of the listeners of one connection snapshot with the new listeners marked.
/// </summary>
public sealed class NetworkListenerView
{
    #region Properties

    /// <summary>
    /// Gets the capture time of the shown snapshot; <see langword="null"/> when there is none.
    /// </summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>
    /// Gets the source as runs.
    /// </summary>
    public IReadOnlyList<TextRun> SourceText { get; init; } = [];

    /// <summary>
    /// Gets the line above the table.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the notes of the panel.
    /// </summary>
    public IReadOnlyList<string> PanelNotes { get; init; } = [];

    /// <summary>
    /// Gets the table of the listeners.
    /// </summary>
    public SnapshotTableView Table { get; init; } = new();

    /// <summary>
    /// Gets the text shown instead of the table; empty when the table is shown.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the panel of a snapshot.
    /// </summary>
    /// <param name="shown">The shown snapshot</param>
    /// <param name="earlier">The comparison snapshot, if any</param>
    /// <returns>The panel</returns>
    public static NetworkListenerView Build(StoredRecord shown, StoredRecord? earlier)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds a panel that shows only a text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The panel</returns>
    public static NetworkListenerView Empty(string text)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // Methods
}