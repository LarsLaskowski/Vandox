using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

#pragma warning disable RH2003, S2325, S4487 // Skeleton: removed with the implementation
/// <summary>
/// Reads the process snapshots of one source for the moment and comparison panels.
/// </summary>
public sealed class ProcessesMomentReader
{
    #region Fields

    /// <summary>
    /// The reader of the snapshots.
    /// </summary>
    private readonly IHistoryReader _reader;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessesMomentReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the snapshots</param>
    public ProcessesMomentReader(IHistoryReader reader)
    {
        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the process snapshot of a source closest to a moment.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="at">The moment</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the moment</returns>
    public Task<HistoryMoment> MomentAsync(string source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the snapshots of a source closest to two points and compares their programs.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="first">The first point</param>
    /// <param name="second">The second point</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the comparison</returns>
    public Task<ProgramComparisonView> CompareAsync(string source, DateTimeOffset first, DateTimeOffset second, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003, S2325, S4487 // Skeleton: removed with the implementation