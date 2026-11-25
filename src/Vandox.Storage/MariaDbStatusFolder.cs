using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Folds the MariaDB status records of a range onto the steps of a time axis: a record holds up to <see cref="Hold"/> (record 0228).
/// </summary>
internal sealed class MariaDbStatusFolder
{
    #region Constants

    /// <summary>
    /// The longest time a status record holds.
    /// </summary>
    internal static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);

    #endregion // Constants

    #region Fields

    private readonly MariaDbStatusQuery _query;

    private readonly UnknownSpans _unknown;

    private readonly long _stepTicks;

    private readonly long _firstStep;

    private readonly int _steps;

    private readonly long[] _knownTicks;

    private readonly long[] _upTicks;

    private readonly StepValueTally?[] _memory;

    private readonly StepValueTally?[] _connections;

    private long _upTotal;

    private long _downTotal;

    private long _notAnsweringTotal;

    private int _records;

    private double? _latestMemory;

    private DateTimeOffset? _latestMemoryAt;

    private DateTimeOffset? _heldAt;

    private string _heldAvailability = string.Empty;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbStatusFolder"/> class.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="unknown">The unknown time of the range</param>
    internal MariaDbStatusFolder(MariaDbStatusQuery query, UnknownSpans unknown)
    {
        _query = query;
        _unknown = unknown;
        _stepTicks = query.Step.Ticks;
        _firstStep = SeriesTime.FloorDiv(query.From.UtcTicks - SeriesTime.EpochTicks, _stepTicks);
        _steps = (int)SeriesTime.Overlapping(query.From.UtcTicks - SeriesTime.EpochTicks, query.To.UtcTicks - SeriesTime.EpochTicks, _stepTicks);
        _knownTicks = new long[_steps];
        _upTicks = new long[_steps];
        _memory = new StepValueTally?[_steps];
        _connections = new StepValueTally?[_steps];
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a record; the records come in capture order.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="availability">The availability of the record</param>
    /// <param name="memoryUsed">The memory in use, <c>null</c> when absent</param>
    /// <param name="threadsConnected">The connections, <c>null</c> when absent</param>
    internal void Add(DateTimeOffset capturedAt, string availability, double? memoryUsed, double? threadsConnected)
    {
        CountHeld(capturedAt);

        _heldAt = capturedAt;
        _heldAvailability = availability;

        if (capturedAt < _query.From || capturedAt >= _query.To)
        {
            return;
        }

        _records++;

        if (availability != MariaDbStatus.Up)
        {
            return;
        }

        var step = (int)(SeriesTime.FloorDiv(capturedAt.UtcTicks - SeriesTime.EpochTicks, _stepTicks) - _firstStep);

        if (memoryUsed is { } memory)
        {
            (_memory[step] ??= new StepValueTally()).Add(memory, capturedAt);
            _latestMemory = memory;
            _latestMemoryAt = capturedAt;
        }

        if (threadsConnected is { } connections)
        {
            (_connections[step] ??= new StepValueTally()).Add(connections, capturedAt);
        }
    }

    /// <summary>
    /// Ends the fold.
    /// </summary>
    /// <param name="oldest">The capture time of the oldest status record, <c>null</c> when none is stored</param>
    /// <returns>The result</returns>
    internal MariaDbStatusResult Finish(DateTimeOffset? oldest)
    {
        CountHeld(_query.To);

        var times = new List<DateTimeOffset>(_steps);
        var up = new List<SeriesPoint>(_steps);

        for (var index = 0; index < _steps; index++)
        {
            var start = new DateTimeOffset(SeriesTime.EpochTicks + ((_firstStep + index) * _stepTicks), TimeSpan.Zero);

            times.Add(start);
            up.Add(UpPoint(index, start));
        }

        var known = TimeSpan.FromTicks(_upTotal + _downTotal + _notAnsweringTotal);

        return new MariaDbStatusResult
               {
                   Times = times,
                   Up = up,
                   Memory = _memory.Select(tally => tally?.ToPoint() ?? new SeriesPoint
                                                                        {
                                                                            State = PointState.Gap
                                                                        }).ToList(),
                   Connections = _connections.Select(tally => tally?.ToPoint() ?? new SeriesPoint
                                                                                  {
                                                                                      State = PointState.Gap
                                                                                  }).ToList(),
                   UpTime = TimeSpan.FromTicks(_upTotal),
                   DownTime = TimeSpan.FromTicks(_downTotal),
                   NotAnsweringTime = TimeSpan.FromTicks(_notAnsweringTotal),
                   UnknownTime = _query.To - _query.From - known,
                   LatestMemory = _latestMemory,
                   LatestMemoryAt = _latestMemoryAt,
                   Oldest = oldest,
                   GapsCompleteUntil = _unknown.KnownUntil,
                   Records = _records
               };
    }

    /// <summary>
    /// Builds the share up of a step.
    /// </summary>
    /// <param name="index">The index of the step</param>
    /// <param name="start">The start of the step</param>
    /// <returns>The point, a gap when the step has no known time</returns>
    private SeriesPoint UpPoint(int index, DateTimeOffset start)
    {
        if (_knownTicks[index] == 0)
        {
            return new SeriesPoint
                   {
                       State = PointState.Gap
                   };
        }

        var share = (double)_upTicks[index] / _knownTicks[index] * 100;

        return new SeriesPoint
               {
                   State = PointState.Value,
                   Minimum = share,
                   Average = share,
                   Maximum = share,
                   Last = share,
                   LastAt = start,
                   Count = 1
               };
    }

    /// <summary>
    /// Counts the time the record held so far up to the next record, at most <see cref="Hold"/>, into the totals and the steps.
    /// </summary>
    /// <param name="next">The capture time of the next record, or the end of the range</param>
    private void CountHeld(DateTimeOffset next)
    {
        if (_heldAt is not { } heldAt)
        {
            return;
        }

        var holdEnd = SeriesTime.Add(heldAt, Hold);
        var start = heldAt > _query.From ? heldAt : _query.From;
        var end = next < holdEnd ? next : holdEnd;

        end = end < _query.To ? end : _query.To;

        if (end <= start || _heldAvailability is not (MariaDbStatus.Up or MariaDbStatus.Down or MariaDbStatus.NotAnswering))
        {
            return;
        }

        foreach (var (from, to) in _unknown.KnownParts(start, end))
        {
            CountPart(from, to);
        }
    }

    /// <summary>
    /// Counts one known part of a held record into its totals and the steps it overlaps.
    /// </summary>
    /// <param name="from">The start of the part</param>
    /// <param name="to">The end of the part</param>
    private void CountPart(DateTimeOffset from, DateTimeOffset to)
    {
        var up = _heldAvailability == MariaDbStatus.Up;
        var fromTicks = from.UtcTicks - SeriesTime.EpochTicks;
        var toTicks = to.UtcTicks - SeriesTime.EpochTicks;
        var total = toTicks - fromTicks;

        if (up)
        {
            _upTotal += total;
        }
        else if (_heldAvailability == MariaDbStatus.Down)
        {
            _downTotal += total;
        }
        else
        {
            _notAnsweringTotal += total;
        }

        var step = SeriesTime.FloorDiv(fromTicks, _stepTicks);

        while (step * _stepTicks < toTicks)
        {
            var overlap = Math.Min(toTicks, (step + 1) * _stepTicks) - Math.Max(fromTicks, step * _stepTicks);
            var index = (int)(step - _firstStep);

            _knownTicks[index] += overlap;
            _upTicks[index] += up ? overlap : 0;
            step++;
        }
    }

    #endregion // Methods
}