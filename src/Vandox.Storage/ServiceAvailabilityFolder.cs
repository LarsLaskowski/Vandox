namespace Vandox.Storage;

/// <summary>
/// Folds the service state records of a range into the availability of each unit: a state holds up to <see cref="Hold"/>, and at most <see cref="MaxUnits"/> units are kept (record 0227).
/// </summary>
internal sealed class ServiceAvailabilityFolder
{
    #region Constants

    /// <summary>
    /// The most units kept.
    /// </summary>
    internal const int MaxUnits = 256;

    /// <summary>
    /// The longest time a recorded state holds.
    /// </summary>
    internal static readonly TimeSpan Hold = TimeSpan.FromMinutes(65);

    #endregion // Constants

    #region Fields

    private readonly DateTimeOffset _from;

    private readonly DateTimeOffset _to;

    private readonly UnknownSpans _unknown;

    private readonly Dictionary<string, ServiceUnitTally> _units = [];

    private string _largest = string.Empty;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceAvailabilityFolder"/> class.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="unknown">The unknown time of the range</param>
    internal ServiceAvailabilityFolder(DateTimeOffset from, DateTimeOffset to, UnknownSpans unknown)
    {
        _from = from;
        _to = to;
        _unknown = unknown;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether a unit name was skipped or evicted.
    /// </summary>
    internal bool Dropped { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a record; the records come in capture order.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="unit">The unit name</param>
    /// <param name="activeState">The active state</param>
    internal void Add(long id, DateTimeOffset capturedAt, string unit, string activeState)
    {
        if (_units.TryGetValue(unit, out var tally))
        {
            Count(tally, capturedAt);

            tally.Id = id;
            tally.CapturedAt = capturedAt;
            tally.ActiveState = activeState;

            return;
        }

        var full = _units.Count >= MaxUnits;

        if (full)
        {
            Dropped = true;

            if (string.CompareOrdinal(unit, _largest) > 0)
            {
                return;
            }

            _units.Remove(_largest);
        }

        _units[unit] = new ServiceUnitTally(unit, id, capturedAt, activeState);

        if (full)
        {
            _largest = _units.Keys.Max(StringComparer.Ordinal)!;
        }
        else if (_units.Count == 1 || string.CompareOrdinal(unit, _largest) > 0)
        {
            _largest = unit;
        }
    }

    /// <summary>
    /// Ends the fold.
    /// </summary>
    /// <returns>The units sorted by name, each with the ID of its latest record</returns>
    internal IReadOnlyList<(ServiceAvailability Row, long LastId)> Finish()
    {
        var rows = new List<(ServiceAvailability Row, long LastId)>(_units.Count);
        var range = _to - _from;

        foreach (var tally in _units.Values.OrderBy(unit => unit.Unit, StringComparer.Ordinal))
        {
            Count(tally, SeriesTime.Add(tally.CapturedAt, Hold));

            rows.Add((new ServiceAvailability
                      {
                          Unit = tally.Unit,
                          ActiveState = tally.ActiveState,
                          Available = tally.Available,
                          NotAvailable = tally.NotAvailable,
                          Unknown = range - tally.Available - tally.NotAvailable,
                          StateAt = tally.CapturedAt
                      },
                      tally.Id));
        }

        return rows;
    }

    /// <summary>
    /// Tells whether a unit in a state counts as available.
    /// </summary>
    /// <param name="activeState">The active state</param>
    /// <returns><see langword="true"/> for <c>active</c>, <c>reloading</c> and <c>refreshing</c></returns>
    private static bool IsAvailable(string activeState)
    {
        return activeState is "active" or "reloading" or "refreshing";
    }

    /// <summary>
    /// Counts the known time the latest state of a unit holds up to the next record, or up to the given end, into the range.
    /// </summary>
    /// <param name="tally">The unit</param>
    /// <param name="next">The capture time of the next record, or the end of the last hold</param>
    private void Count(ServiceUnitTally tally, DateTimeOffset next)
    {
        var holdEnd = SeriesTime.Add(tally.CapturedAt, Hold);
        var start = tally.CapturedAt > _from ? tally.CapturedAt : _from;
        var end = next < holdEnd ? next : holdEnd;

        end = end < _to ? end : _to;

        if (end <= start)
        {
            return;
        }

        var known = _unknown.KnownTime(start, end);

        if (IsAvailable(tally.ActiveState))
        {
            tally.Available += known;
        }
        else
        {
            tally.NotAvailable += known;
        }
    }

    #endregion // Methods
}