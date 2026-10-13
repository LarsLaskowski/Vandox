using Vandox.Storage;

namespace Vandox.Backend.Logs;

#pragma warning disable RH2003, S2325

/// <summary>
/// A log line prepared for display: its external text is already escaped and capped.
/// </summary>
public sealed class LogLineView
{
    #region Properties

    /// <summary>
    /// Gets or sets the record ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Gets or sets the severity name, or <c>-</c>.
    /// </summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the process ID.
    /// </summary>
    public int Pid { get; set; }

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    public IReadOnlyList<TextRun> Source { get; set; } = [];

    /// <summary>
    /// Gets or sets the host.
    /// </summary>
    public IReadOnlyList<TextRun> Host { get; set; } = [];

    /// <summary>
    /// Gets or sets the program.
    /// </summary>
    public IReadOnlyList<TextRun> Program { get; set; } = [];

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public IReadOnlyList<TextRun> Message { get; set; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the view of a stored log line.
    /// </summary>
    /// <param name="record">The stored record</param>
    /// <returns>The view</returns>
    public static LogLineView From(StoredRecord record)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}