using Vandox.Backend.History;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

/// <summary>
/// Reads the process snapshots of one source for the moment and comparison panels.
/// </summary>
public sealed class ProcessesMomentReader
{
    #region Fields

    /// <summary>
    /// The reader of the closest snapshots.
    /// </summary>
    private readonly HistoryMomentReader _moments;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessesMomentReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the snapshots</param>
    public ProcessesMomentReader(IHistoryReader reader)
    {
        _moments = new HistoryMomentReader(reader);
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
    public async Task<HistoryMoment> MomentAsync(string source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var closest = await _moments.ClosestAsync(RecordKind.ProcessSnapshot, source, at, cancellationToken).ConfigureAwait(false);

        return new HistoryMoment
               {
                   At = at,
                   Process = closest is null ? null : ProcessSnapshotView.FromProcesses(closest.Record),
                   ProcessBefore = closest?.Before
               };
    }

    /// <summary>
    /// Reads the snapshots of a source closest to two points and compares their programs.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="first">The first point</param>
    /// <param name="second">The second point</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the comparison</returns>
    public async Task<ProgramComparisonView> CompareAsync(string source, DateTimeOffset first, DateTimeOffset second, CancellationToken cancellationToken)
    {
        var firstIsEarlier = first <= second;
        var earlier = await FiguresAsync(source, firstIsEarlier ? first : second, cancellationToken).ConfigureAwait(false);

        if (earlier is null)
        {
            return ProgramComparisonView.Missing(firstIsEarlier ? ProcessesView.NoSnapshotFirstPointText : ProcessesView.NoSnapshotSecondPointText);
        }

        var later = await FiguresAsync(source, firstIsEarlier ? second : first, cancellationToken).ConfigureAwait(false);

        if (later is null)
        {
            return ProgramComparisonView.Missing(firstIsEarlier ? ProcessesView.NoSnapshotSecondPointText : ProcessesView.NoSnapshotFirstPointText);
        }

        return earlier.Value.Id == later.Value.Id ? ProgramComparisonView.Missing(ProcessesView.SameSnapshotText) : ProgramComparisonView.Compare(earlier.Value.Figures, later.Value.Figures);
    }

    /// <summary>
    /// Reads the snapshot of a source closest to a point and reduces it to its figures, so that the record is not held beyond this read.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="at">The point</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the ID of the record and its figures, or <see langword="null"/> when there is no snapshot</returns>
    private async Task<(long Id, ProgramFigures Figures)?> FiguresAsync(string source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var closest = await _moments.ClosestAsync(RecordKind.ProcessSnapshot, source, at, cancellationToken).ConfigureAwait(false);

        return closest is null ? null : (closest.Record.Id, ProgramFigures.FromRecord(closest.Record));
    }

    #endregion // Methods
}