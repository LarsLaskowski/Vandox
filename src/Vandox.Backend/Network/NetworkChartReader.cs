using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the two charts of the network view.
/// </summary>
public sealed class NetworkChartReader
{
    #region Fields

    /// <summary>
    /// The reader of the rollups.
    /// </summary>
    private readonly IConnectionRollupReader _reader;

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
    /// Initializes a new instance of the <see cref="NetworkChartReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the rollups</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="clock">The clock</param>
    public NetworkChartReader(IConnectionRollupReader reader, RetentionPolicy policy, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);

        _reader = reader;
        _policy = policy;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the sources and the two charts of a range.
    /// </summary>
    /// <param name="source">The source, <see langword="null"/> for the default source</param>
    /// <param name="range">The range</param>
    /// <param name="chartWidth">The width of the chart in pixels</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the charts</returns>
    public async Task<NetworkCharts> ReadAsync(string? source, HistoryRange range, double chartWidth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        var sources = await _reader.ConnectionSourcesAsync(new ProgramSourceQuery
                                                           {
                                                               From = range.Start,
                                                               To = range.End
                                                           },
                                                           cancellationToken).ConfigureAwait(false);
        var names = sources.Sources.Select(listed => listed.Source).ToList();
        var chosen = source ?? SourceChoice.Latest(sources);

        if (chosen.Length == 0)
        {
            return new NetworkCharts
                   {
                       Sources = names,
                       Notes = [NetworkView.NoDataText]
                   };
        }

        var step = SeriesLadder.Choose(range.Start, range.End, _clock.GetUtcNow(), HistoryView.Target(chartWidth), _policy);
        var rollups = await _reader.ConnectionRollupsAsync(new ConnectionRollupQuery
                                                           {
                                                               Source = chosen,
                                                               From = range.Start,
                                                               To = range.End,
                                                               Step = step
                                                           },
                                                           cancellationToken).ConfigureAwait(false);
        var notes = NetworkChartView.ChartNotes(rollups, sources, range, step);

        if (rollups.Times.Count == 0)
        {
            return new NetworkCharts
                   {
                       Sources = names,
                       Source = chosen,
                       Step = step,
                       Notes = notes
                   };
        }

        return new NetworkCharts
               {
                   Sources = names,
                   Source = chosen,
                   Step = step,
                   States = NetworkChartView.StateChart(rollups),
                   Programs = NetworkChartView.ProgramChart(rollups),
                   Notes = notes
               };
    }

    #endregion // Methods
}