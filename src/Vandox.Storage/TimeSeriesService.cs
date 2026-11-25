namespace Vandox.Storage;

/// <summary>
/// Answers time-series queries in process: metric lines at one resolution on one time axis, with the gaps and markers of the range
/// ([Time-series queries](../../docs/areas/time-series-query.md)).
/// </summary>
public sealed class TimeSeriesService
{
    #region Constants

    /// <summary>
    /// The most series of a query.
    /// </summary>
    public const int MaxSeries = 16;

    /// <summary>
    /// The most lines of a result.
    /// </summary>
    public const int MaxLines = 64;

    /// <summary>
    /// The largest target.
    /// </summary>
    public const int MaxTarget = 2000;

    /// <summary>
    /// The most markers of a result.
    /// </summary>
    public const int MaxMarkers = 1000;

    /// <summary>
    /// The most gaps of a result.
    /// </summary>
    public const int MaxGaps = 1000;

    #endregion // Constants

    #region Fields

    private readonly ITimeSeriesReader _reader;

    private readonly RetentionPolicy _policy;

    private readonly TimeProvider _clock;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeSeriesService"/> class.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="policy">The retention periods that bound the resolutions</param>
    /// <param name="clock">The clock of the query time</param>
    public TimeSeriesService(ITimeSeriesReader reader, RetentionPolicy policy, TimeProvider clock)
    {
        _reader = reader;
        _policy = policy;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Runs a query: the lines of its series at one resolution, and the gaps and markers of its range. Nothing after the query time is returned.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the query; no partial result is returned</param>
    /// <returns>A task that returns the result</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule; nothing was read</exception>
    /// <exception cref="StoreException">A store read fails</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled</exception>
    public async Task<TimeSeriesResult> QueryAsync(TimeSeriesQuery query, CancellationToken cancellationToken)
    {
        var series = Validate(query);

        cancellationToken.ThrowIfCancellationRequested();

        var now = _clock.GetUtcNow();

        if (query.From >= now)
        {
            return new TimeSeriesResult();
        }

        var range = new SeriesRange
                    {
                        From = query.From,
                        End = query.To < now ? query.To : now,
                        Now = now,
                        Target = query.Target
                    };
        var data = await SeriesLineReader.ReadAsync(_reader, _policy, series, range, query.StepsOnly, cancellationToken).ConfigureAwait(false);
        var gaps = await SeriesGapReader.ReadAsync(_reader, range, cancellationToken).ConfigureAwait(false);
        (List<SeriesMarker> Markers, DateTimeOffset? CompleteUntil) markers = ([], null);

        if (query.SkipMarkers == false)
        {
            markers = await SeriesMarkerReader.ReadAsync(_reader, range, cancellationToken).ConfigureAwait(false);
        }

        var startsBefore = _policy.HourlyRollups is not null && query.From < now - _policy.HourlyRollups.Value;

        if (data.Raw)
        {
            var (times, lines) = SeriesAssembler.Raw(data.Lines, gaps.Gaps);

            return new TimeSeriesResult
                   {
                       Resolution = TimeSeriesResolution.Raw,
                       Times = times,
                       Lines = lines,
                       CutSeries = data.Cut,
                       StartsBeforeRollups = startsBefore,
                       GapsCompleteUntil = gaps.CompleteUntil,
                       MarkersCompleteUntil = markers.CompleteUntil,
                       Gaps = gaps.Gaps,
                       Markers = markers.Markers
                   };
        }

        return new TimeSeriesResult
               {
                   Resolution = TimeSeriesResolution.Step,
                   Step = data.Step,
                   Times = data.Axis.Select(time => new DateTimeOffset(time, TimeSpan.Zero)).ToList(),
                   Lines = SeriesAssembler.Steps(data),
                   CutSeries = data.Cut,
                   StartsBeforeRollups = startsBefore,
                   CompleteUntil = data.CompleteUntil,
                   GapsCompleteUntil = gaps.CompleteUntil,
                   MarkersCompleteUntil = markers.CompleteUntil,
                   Gaps = gaps.Gaps,
                   Markers = markers.Markers
               };
    }

    /// <summary>
    /// Reads the service restarts of a range with the rule of the time-series markers, without a series.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range; clamped to the query time</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the restarts in time order, at most <see cref="MaxMarkers"/>, and the time up to which the list is complete</returns>
    /// <exception cref="InvalidQueryException">The range breaks a rule; nothing was read</exception>
    /// <exception cref="StoreException">A store read fails</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled</exception>
    public async Task<SeriesMarkerList> RestartsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        RecordQueries.Require(from < to, "from must be before to");
        RecordQueries.Require(StorageTime.InStorableRange(from) && StorageTime.InStorableRange(to), "from and to must be within the storable range");
        cancellationToken.ThrowIfCancellationRequested();

        var now = _clock.GetUtcNow();

        if (from >= now)
        {
            return new SeriesMarkerList();
        }

        var range = new SeriesRange
                    {
                        From = from,
                        End = to < now ? to : now,
                        Now = now,
                        Target = 1
                    };
        var restarts = await SeriesMarkerReader.ReadRestartsOnlyAsync(_reader, range, cancellationToken).ConfigureAwait(false);

        return new SeriesMarkerList
               {
                   Markers = restarts.Markers,
                   CompleteUntil = restarts.CompleteUntil
               };
    }

    /// <summary>
    /// Checks a query against the rules of the plan and returns its distinct series.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The series, each once, in query order</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    private static List<SeriesKey> Validate(TimeSeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(query.Series.Count > 0, "at least one series is required");
        RecordQueries.Require(query.Series.Count <= MaxSeries, $"at most {MaxSeries} series");
        RecordQueries.Require(query.Series.All(key => string.IsNullOrEmpty(key.Source) == false && string.IsNullOrEmpty(key.Name) == false), "source and name are required");
        RecordQueries.Require(query.Target >= 1 && query.Target <= MaxTarget, $"target must be 1 to {MaxTarget}");
        RecordQueries.Require(query.From < query.To, "from must be before to");
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "from and to must be within the storable range");

        return query.Series.DistinctBy(key => (key.Source, key.Name)).ToList();
    }

    #endregion // Methods
}