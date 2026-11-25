using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// Reads the section Services: the restarts and the availability of the units.
/// </summary>
public sealed class ServicesReader
{
    #region Fields

    /// <summary>
    /// The time-series service that reads the restarts.
    /// </summary>
    private readonly TimeSeriesService _series;

    /// <summary>
    /// The reader of the availability.
    /// </summary>
    private readonly IServiceHistoryReader _reader;

    /// <summary>
    /// The clock.
    /// </summary>
    private readonly TimeProvider _clock;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ServicesReader"/> class.
    /// </summary>
    /// <param name="series">The time-series service</param>
    /// <param name="reader">The reader of the availability</param>
    /// <param name="clock">The clock</param>
    public ServicesReader(TimeSeriesService series, IServiceHistoryReader reader, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(clock);

        _series = series;
        _reader = reader;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the section of a range, up to now.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the section</returns>
    public async Task<ServicesTable> ReadAsync(HistoryRange range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        var now = _clock.GetUtcNow();
        var end = range.End < now ? range.End : now;
        var restarts = await _series.RestartsAsync(range.Start, end, cancellationToken).ConfigureAwait(false);
        var availability = await _reader.ServiceAvailabilityAsync(new ServiceAvailabilityQuery
                                                                  {
                                                                      From = range.Start,
                                                                      To = end
                                                                  },
                                                                  cancellationToken).ConfigureAwait(false);

        return ServicesTableView.Build(availability, restarts, new HistoryRange(range.Start, end));
    }

    #endregion // Methods
}