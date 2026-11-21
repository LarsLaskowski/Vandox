using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

#pragma warning disable RH2003, S2325, S4487 // Skeleton: removed with the implementation
/// <summary>
/// Reads the two charts of the processes view.
/// </summary>
public sealed class ProcessesChartReader
{
    #region Fields

    /// <summary>
    /// The reader of the rollups.
    /// </summary>
    private readonly IProgramRollupReader _reader;

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
    /// Initializes a new instance of the <see cref="ProcessesChartReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the rollups</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="clock">The clock</param>
    public ProcessesChartReader(IProgramRollupReader reader, RetentionPolicy policy, TimeProvider clock)
    {
        _reader = reader;
        _policy = policy;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the source list and the memory and CPU charts of a range.
    /// </summary>
    /// <param name="source">The source; <see langword="null"/> is the one with the latest bucket</param>
    /// <param name="range">The range</param>
    /// <param name="chartWidth">The width of the charts in pixels</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the charts</returns>
    public Task<ProcessesCharts> ReadAsync(string? source, HistoryRange range, double chartWidth, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003, S2325, S4487 // Skeleton: removed with the implementation