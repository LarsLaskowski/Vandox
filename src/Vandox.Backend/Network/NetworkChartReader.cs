using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the two charts of the network view.
/// </summary>
public sealed class NetworkChartReader
{
    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The reader of the rollups.
    /// </summary>
    private readonly IConnectionRollupReader _reader;
#pragma warning restore S4487

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The retention policy.
    /// </summary>
    private readonly RetentionPolicy _policy;
#pragma warning restore S4487

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The clock.
    /// </summary>
    private readonly TimeProvider _clock;
#pragma warning restore S4487

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
        _reader = reader;
        _policy = policy;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable S2325
    /// <summary>
    /// Reads the sources and the two charts of a range.
    /// </summary>
    /// <param name="source">The source, <see langword="null"/> for the default source</param>
    /// <param name="range">The range</param>
    /// <param name="chartWidth">The width of the chart in pixels</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the charts</returns>
    public Task<NetworkCharts> ReadAsync(string? source, HistoryRange range, double chartWidth, CancellationToken cancellationToken)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }
#pragma warning restore S2325

    #endregion // Methods
}