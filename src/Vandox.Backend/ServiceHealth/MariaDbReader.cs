using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// Reads the section MariaDB: the metric series and the status records.
/// </summary>
public sealed class MariaDbReader
{
    #region Fields

    /// <summary>
    /// The metrics of the section: the theoretical maximum of the memory, the RAM, the queries per second and the two shares of the buffer pool.
    /// </summary>
    private static readonly SeriesKey[] _metrics = [
                                                       new SeriesKey(MariaDbChartView.MariaDbSource, MariaDbChartView.MaximumName),
                                                       new SeriesKey(MariaDbChartView.SystemSource, MariaDbChartView.RamName),
                                                       new SeriesKey(MariaDbChartView.MariaDbSource, MariaDbChartView.QueriesName),
                                                       new SeriesKey(MariaDbChartView.MariaDbSource, MariaDbChartView.UsedName),
                                                       new SeriesKey(MariaDbChartView.MariaDbSource, MariaDbChartView.HitName)
                                                   ];

    /// <summary>
    /// The time-series service that reads the metrics.
    /// </summary>
    private readonly TimeSeriesService _series;

    /// <summary>
    /// The reader of the status records.
    /// </summary>
    private readonly IMariaDbHistoryReader _reader;

    /// <summary>
    /// The retention policy.
    /// </summary>
    private readonly RetentionPolicy _policy;

    /// <summary>
    /// The clock.
    /// </summary>
    private readonly TimeProvider _clock;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbReader"/> class.
    /// </summary>
    /// <param name="series">The time-series service</param>
    /// <param name="reader">The reader of the status records</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="clock">The clock</param>
    public MariaDbReader(TimeSeriesService series, IMariaDbHistoryReader reader, RetentionPolicy policy, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);

        _series = series;
        _reader = reader;
        _policy = policy;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the section of a range: the metrics in steps without markers, then the status at the step of that read.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="chartWidth">The width of the charts in pixels</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the section</returns>
    public async Task<MariaDbCharts> ReadAsync(HistoryRange range, double chartWidth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        var now = _clock.GetUtcNow();
        var series = await _series.QueryAsync(new TimeSeriesQuery
                                              {
                                                  Series = _metrics,
                                                  From = range.Start,
                                                  To = range.End,
                                                  Target = HistoryView.Target(chartWidth),
                                                  StepsOnly = true,
                                                  SkipMarkers = true
                                              },
                                              cancellationToken).ConfigureAwait(false);

        if (series.Step is not { } step)
        {
            return MariaDbChartView.Build(series, new MariaDbStatusResult(), range, _policy.Snapshots, now);
        }

        var status = await _reader.MariaDbStatusAsync(new MariaDbStatusQuery
                                                      {
                                                          From = range.Start,
                                                          To = range.End < now ? range.End : now,
                                                          Step = step
                                                      },
                                                      cancellationToken).ConfigureAwait(false);

        return MariaDbChartView.Build(series, status, range, _policy.Snapshots, now);
    }

    #endregion // Methods
}