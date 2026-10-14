namespace Vandox.Storage;

/// <summary>
/// What a scan has found so far: the positions to return, the lines waiting for the line index, and how far it got.
/// </summary>
internal sealed class ScanState
{
    #region Properties

    /// <summary>
    /// Gets the positions of the lines to return, in order.
    /// </summary>
    internal List<LogPosition> Kept { get; } = [];

    /// <summary>
    /// Gets the lines that pass every test but the multi-part terms and wait for the line index.
    /// </summary>
    internal List<(LogPosition Position, string Message)> Pending { get; } = [];

    /// <summary>
    /// Gets or sets the number of characters of the messages in <see cref="Pending"/>.
    /// </summary>
    internal int PendingCharacters { get; set; }

    /// <summary>
    /// Gets or sets the position of the last examined line.
    /// </summary>
    internal LogPosition? Last { get; set; }

    /// <summary>
    /// Gets or sets the number of examined lines.
    /// </summary>
    internal int Examined { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page budget ended the scan.
    /// </summary>
    internal bool ClockStopped { get; set; }

    #endregion // Properties
}