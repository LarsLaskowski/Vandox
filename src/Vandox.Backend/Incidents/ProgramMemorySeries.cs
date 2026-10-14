using Vandox.Backend.Components.Charts;
using Vandox.Backend.Logs;
using Vandox.Core.Incidents;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Collects the memory per program of the snapshots of a window into a stacked chart.
/// </summary>
public sealed class ProgramMemorySeries
{
    #region Constants

    private const double BytesPerMib = 1048576.0;

    #endregion // Constants

    #region Fields

    private readonly List<DateTimeOffset> _times = [];

    private readonly Dictionary<string, List<double>> _tracked = new(StringComparer.Ordinal);

    private readonly List<double> _overflow = [];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramMemorySeries"/> class.
    /// </summary>
    public ProgramMemorySeries()
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of snapshots added.
    /// </summary>
    public int Count => _times.Count;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot.
    /// </summary>
    /// <param name="capturedAt">The time of the snapshot</param>
    /// <param name="programs">The memory per program</param>
    /// <returns><c>false</c> when the snapshot is skipped because it is not later than the previous one</returns>
    public bool Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);

        if (_times.Count > 0 && capturedAt <= _times[^1])
        {
            return false;
        }

        var index = _times.Count;

        _times.Add(capturedAt);
        _overflow.Add(0);

        foreach (var values in _tracked.Values)
        {
            values.Add(0);
        }

        foreach (var (name, usage) in programs)
        {
            var mib = usage.RssBytes / BytesPerMib;

            if (_tracked.TryGetValue(name, out var values))
            {
                values[index] = mib;
            }
            else if (_tracked.Count < IncidentsView.MaxChartPrograms)
            {
                values = [.. Enumerable.Repeat(0.0, index + 1)];
                values[index] = mib;
                _tracked[name] = values;
            }
            else
            {
                _overflow[index] += mib;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the stacked chart: the five programs with the highest peak and the other programs summed, in MiB.
    /// </summary>
    /// <returns>The chart, or <c>null</c> without a snapshot</returns>
    public ChartData? ToChart()
    {
        if (_times.Count == 0)
        {
            return null;
        }

        var ranked = _tracked.OrderByDescending(pair => pair.Value.Max())
                             .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                             .ToList();
        var series = ranked.Take(IncidentsView.ChartPrograms)
                           .Select(pair => new ChartSeries(LogText.Escape(pair.Key, IncidentsView.MaxLabelChars), [.. pair.Value.Select(value => (double?)value)]))
                           .ToList();
        var rest = ranked.Skip(IncidentsView.ChartPrograms).Select(pair => pair.Value).ToList();
        var others = new double?[_times.Count];

        for (var index = 0; index < others.Length; index++)
        {
            others[index] = _overflow[index] + rest.Sum(values => values[index]);
        }

        series.Add(new ChartSeries(IncidentsView.OtherPrograms, others));

        return new ChartData(_times, series, true);
    }

    #endregion // Methods
}