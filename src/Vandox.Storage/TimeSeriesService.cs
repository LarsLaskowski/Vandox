namespace Vandox.Storage;

/// <summary>
/// Answers time-series queries in process: lines on one axis, gaps and markers.
/// </summary>
public sealed class TimeSeriesService
{
    #region Constants

    /// <summary>
    /// The maximum number of series of a query.
    /// </summary>
    public const int MaxSeries = 16;

    /// <summary>
    /// The maximum number of lines of a result.
    /// </summary>
    public const int MaxLines = 64;

    /// <summary>
    /// The maximum target of axis times.
    /// </summary>
    public const int MaxTarget = 2000;

    /// <summary>
    /// The maximum number of markers of a result.
    /// </summary>
    public const int MaxMarkers = 1000;

    /// <summary>
    /// The maximum number of gaps of a result.
    /// </summary>
    public const int MaxGaps = 1000;

    #endregion // Constants

    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The store reads.
    /// </summary>
    private readonly ITimeSeriesReader _reader;

    /// <summary>
    /// The retention policy.
    /// </summary>
    private readonly RetentionPolicy _policy;

    /// <summary>
    /// The clock.
    /// </summary>
    private readonly TimeProvider _clock;
#pragma warning restore S4487

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeSeriesService"/> class.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="clock">The clock</param>
    public TimeSeriesService(ITimeSeriesReader reader, RetentionPolicy policy, TimeProvider clock)
    {
        _reader = reader;
        _policy = policy;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
    /// <summary>
    /// Answers a time-series query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the result</returns>
    public Task<TimeSeriesResult> QueryAsync(TimeSeriesQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003

    #endregion // Methods
}