using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// A record emitter double that keeps what a parser emits and validates every record as the importer does.
/// </summary>
internal sealed class RecordingEmitter : IRecordEmitter
{
    #region Fields

    private readonly List<DataRecord> _records = [];
    private readonly List<(long Line, string Reason)> _skips = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the records that passed the validation, in the order they were emitted.
    /// </summary>
    internal IReadOnlyList<DataRecord> Records => _records;

    /// <summary>
    /// Gets the skips, including the refusals of records, in the order they were reported.
    /// </summary>
    internal IReadOnlyList<(long Line, string Reason)> Skips => _skips;

    /// <summary>
    /// Gets the number of calls of <see cref="RecordAsync"/>, the refused ones and the one that failed included.
    /// </summary>
    internal int Calls { get; private set; }

    /// <summary>
    /// Gets or sets a function that returns the exception <see cref="RecordAsync"/> throws for the call with the given 1-based number and record, or <c>null</c>.
    /// </summary>
    internal Func<int, DataRecord, Exception?>? Failure { get; set; }

    /// <summary>
    /// Gets or sets an action that runs for every record that passed the validation, after it was kept.
    /// </summary>
    internal Action<DataRecord>? Observed { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Runs a parser over bytes and returns the emitter that saw the result.
    /// </summary>
    /// <param name="parser">The parser</param>
    /// <param name="file">The file the bytes stand for</param>
    /// <param name="content">The bytes</param>
    /// <param name="cancellationToken">The token the parser honors</param>
    /// <returns>A task that returns the emitter</returns>
    internal static async Task<RecordingEmitter> ParseAsync(ILogParser parser, LogFile file, byte[] content, CancellationToken cancellationToken)
    {
        var emitter = new RecordingEmitter();

        using var input = new MemoryStream(content);

        await parser.ParseAsync(file, input, emitter, cancellationToken);

        return emitter;
    }

    /// <summary>
    /// Runs a parser over text and returns the emitter that saw the result.
    /// </summary>
    /// <param name="parser">The parser</param>
    /// <param name="file">The file the text stands for</param>
    /// <param name="content">The text, written as UTF-8</param>
    /// <param name="cancellationToken">The token the parser honors</param>
    /// <returns>A task that returns the emitter</returns>
    internal static Task<RecordingEmitter> ParseAsync(ILogParser parser, LogFile file, string content, CancellationToken cancellationToken)
    {
        return ParseAsync(parser, file, System.Text.Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    /// <summary>
    /// Returns the log line of a record.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The payload</returns>
    internal static LogLine Line(DataRecord record)
    {
        return (LogLine)record.Data!;
    }

    /// <summary>
    /// Describes a record with all its fields, so that two sequences can be compared as text.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The description</returns>
    internal static string Describe(DataRecord record)
    {
        var line = Line(record);
        var priority = line.Priority is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";

        return $"{record.Origin}|{record.Source}|{record.Seq}|{record.CapturedAt.UtcTicks}|{line.Log}|{line.Host}|{line.Program}|{line.Pid}|{priority}|{line.Truncated}|{line.Event}|{line.Message}";
    }

    #endregion // Methods

    #region IRecordEmitter

    /// <inheritdoc />
    public ValueTask RecordAsync(DataRecord record, CancellationToken cancellationToken)
    {
        Calls++;

        var failure = Failure?.Invoke(Calls, record);

        if (failure is not null)
        {
            throw failure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var error = record.Validate();

        if (error is not null)
        {
            _skips.Add((0, "record refused: " + error.Message));

            return ValueTask.CompletedTask;
        }

        if (StorableTime.Contains(record.CapturedAt) && record.Origin == RecordOrigin.Import)
        {
            _records.Add(record);
            Observed?.Invoke(record);
        }
        else
        {
            _skips.Add((0, "record refused: captured_at or origin"));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Skip(long line, string reason)
    {
        _skips.Add((line, reason));
    }

    #endregion // IRecordEmitter
}