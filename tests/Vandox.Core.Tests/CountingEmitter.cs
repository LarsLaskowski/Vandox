using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// A record emitter double that validates every record as the importer does, keeps none of them and counts what it
/// saw: the records, the skips, and the records per event and per priority.
/// </summary>
internal sealed class CountingEmitter : IRecordEmitter
{
    #region Fields

    private readonly Dictionary<string, long> _perEvent = [];
    private readonly Dictionary<int, long> _perPriority = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of records that passed the validation.
    /// </summary>
    internal long Records { get; private set; }

    /// <summary>
    /// Gets the number of skips, including the refusals of records.
    /// </summary>
    internal long Skips { get; private set; }

    /// <summary>
    /// Gets the reason of the first skip, or an empty text.
    /// </summary>
    internal string FirstSkip { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the number of records per event; the key of a record without an event is the empty text.
    /// </summary>
    internal IReadOnlyDictionary<string, long> PerEvent => _perEvent;

    /// <summary>
    /// Gets the number of records per priority; the key of a record without a priority is -1.
    /// </summary>
    internal IReadOnlyDictionary<int, long> PerPriority => _perPriority;

    /// <summary>
    /// Gets or sets an action that runs for every record that passed the validation, after it was counted.
    /// </summary>
    internal Action<DataRecord>? Observed { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the number of records with an event.
    /// </summary>
    /// <param name="name">The event</param>
    /// <returns>The number, 0 for an event that did not occur</returns>
    internal long CountOf(string name)
    {
        return _perEvent.GetValueOrDefault(name);
    }

    /// <summary>
    /// Returns the number of records with a priority.
    /// </summary>
    /// <param name="priority">The priority, -1 for none</param>
    /// <returns>The number, 0 for a priority that did not occur</returns>
    internal long CountOfPriority(int priority)
    {
        return _perPriority.GetValueOrDefault(priority);
    }

    /// <summary>
    /// Counts an accepted record and runs the observer.
    /// </summary>
    /// <param name="record">The record</param>
    private void Count(DataRecord record)
    {
        Records++;

        if (record.Data is LogLine line)
        {
            var priority = line.Priority is { } value ? value : -1;

            _perEvent[line.Event] = _perEvent.GetValueOrDefault(line.Event) + 1;
            _perPriority[priority] = _perPriority.GetValueOrDefault(priority) + 1;
        }

        Observed?.Invoke(record);
    }

    /// <summary>
    /// Counts a skip.
    /// </summary>
    /// <param name="reason">The reason</param>
    private void CountSkip(string reason)
    {
        if (Skips == 0)
        {
            FirstSkip = reason;
        }

        Skips++;
    }

    #endregion // Methods

    #region IRecordEmitter

    /// <inheritdoc />
    public ValueTask RecordAsync(DataRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var error = record.Validate();

        if (error is not null)
        {
            CountSkip("record refused: " + error.Message);

            return ValueTask.CompletedTask;
        }

        if (StorableTime.Contains(record.CapturedAt) && record.Origin == RecordOrigin.Import)
        {
            Count(record);
        }
        else
        {
            CountSkip("record refused: captured_at or origin");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Skip(long line, string reason)
    {
        CountSkip(reason);
    }

    #endregion // IRecordEmitter
}