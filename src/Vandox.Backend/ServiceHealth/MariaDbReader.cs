using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

#pragma warning disable S4487, S2325, RH2003 // Skeleton: removed with the implementation
/// <summary>
/// Reads the section MariaDB: the metric series and the status records.
/// </summary>
public sealed class MariaDbReader
{
    #region Fields

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
        _series = series;
        _reader = reader;
        _policy = policy;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the section of a range.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="chartWidth">The width of the charts in pixels</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the section</returns>
    public Task<MariaDbCharts> ReadAsync(HistoryRange range, double chartWidth, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore S4487, S2325, RH2003 // Skeleton: removed with the implementation