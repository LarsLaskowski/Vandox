using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

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
    public async Task<ProcessesCharts> ReadAsync(string? source, HistoryRange range, double chartWidth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        var sources = await _reader.ProgramSourcesAsync(new ProgramSourceQuery
                                                        {
                                                            From = range.Start,
                                                            To = range.End
                                                        },
                                                        cancellationToken).ConfigureAwait(false);
        var names = sources.Sources.Select(listed => listed.Source).ToList();
        var chosen = source ?? SourceChoice.Latest(sources);

        if (chosen.Length == 0)
        {
            return new ProcessesCharts
                   {
                       Sources = names,
                       Notes = [ProcessesView.NoDataText]
                   };
        }

        var step = SeriesLadder.Choose(range.Start, range.End, _clock.GetUtcNow(), HistoryView.Target(chartWidth), _policy);
        var memory = await ReadAsync(chosen, range, step, ProgramMeasure.Memory, cancellationToken).ConfigureAwait(false);
        var notes = ProcessesChartView.Notes(memory, sources, range, step);

        if (memory.Times.Count == 0)
        {
            return new ProcessesCharts
                   {
                       Sources = names,
                       Source = chosen,
                       Notes = notes
                   };
        }

        var cpu = await ReadAsync(chosen, range, step, ProgramMeasure.Cpu, cancellationToken).ConfigureAwait(false);
        var memoryView = ProcessesChartView.Build(memory, ProgramMeasure.Memory, range, null);

        return new ProcessesCharts
               {
                   Sources = names,
                   Source = chosen,
                   Memory = memoryView,
                   Cpu = ProcessesChartView.Build(cpu, ProgramMeasure.Cpu, range, null),
                   MemoryNote = memoryView.Notes.Count > 0 ? memoryView.Notes[0] : string.Empty,
                   Notes = notes
               };
    }

    /// <summary>
    /// Reads the top programs of a source by a measure.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="range">The range</param>
    /// <param name="step">The step</param>
    /// <param name="measure">The measure</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    private Task<ProgramRollupResult> ReadAsync(string source, HistoryRange range, TimeSpan step, ProgramMeasure measure, CancellationToken cancellationToken)
    {
        return _reader.ProgramRollupsAsync(new ProgramRollupQuery
                                           {
                                               Source = source,
                                               From = range.Start,
                                               To = range.End,
                                               Step = step,
                                               Measure = measure,
                                               Count = ProcessesView.Programs
                                           },
                                           cancellationToken);
    }

    #endregion // Methods
}