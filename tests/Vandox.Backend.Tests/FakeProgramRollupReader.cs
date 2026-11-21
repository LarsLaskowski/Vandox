using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A per-program rollup reader that answers from scripted results and source lists, records its calls in order, can hold a call until released and can fail.
/// </summary>
internal sealed class FakeProgramRollupReader : IProgramRollupReader, IDisposable
{
    #region Fields

    /// <summary>
    /// Guards the recorded calls and the counters.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The recorded calls.
    /// </summary>
    private readonly List<(string Name, object? Argument, CancellationToken Token)> _calls = [];

    /// <summary>
    /// The recorded call sequence.
    /// </summary>
    private readonly List<string> _sequence = [];

    /// <summary>
    /// Released once per call that entered.
    /// </summary>
    private readonly SemaphoreSlim _entered = new(0);

    /// <summary>
    /// The number of calls running.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// The highest number of calls that ran at once.
    /// </summary>
    private int _maxInFlight;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets a task every call waits for before it answers, or <c>null</c> to answer at once.
    /// </summary>
    internal Task? Hold { get; set; }

    /// <summary>
    /// Gets or sets an exception every call throws after it entered, or <c>null</c> to answer.
    /// </summary>
    internal Exception? Failure { get; set; }

    /// <summary>
    /// Gets or sets an action that runs after each call entered, or <c>null</c> for none.
    /// </summary>
    internal Action? Entered { get; set; }

    /// <summary>
    /// Gets or sets the answer of <see cref="ProgramSourcesAsync"/>.
    /// </summary>
    internal ProgramSourceList Sources { get; set; } = new();

    /// <summary>
    /// Gets the scripted results by measure; a measure without one is answered with an empty result.
    /// </summary>
    internal Dictionary<ProgramMeasure, ProgramRollupResult> Results { get; } = [];

    /// <summary>
    /// Gets or sets a function that gives the result of a query instead of <see cref="Results"/>, or <c>null</c> for none.
    /// </summary>
    internal Func<ProgramRollupQuery, ProgramRollupResult>? Answer { get; set; }

    /// <summary>
    /// Gets the recorded calls: the member name, its argument and its token.
    /// </summary>
    internal IReadOnlyList<(string Name, object? Argument, CancellationToken Token)> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>
    /// Gets the call sequence as <c>Sources</c> and <c>Rollups &lt;measure&gt;</c>.
    /// </summary>
    internal IReadOnlyList<string> Sequence
    {
        get
        {
            lock (_lock)
            {
                return [.. _sequence];
            }
        }
    }

    /// <summary>
    /// Gets the queries of the rollup reads in order.
    /// </summary>
    internal IReadOnlyList<ProgramRollupQuery> Queries => [.. Calls.Where(call => call.Argument is ProgramRollupQuery).Select(call => (ProgramRollupQuery)call.Argument!)];

    /// <summary>
    /// Gets the highest number of calls that ran at once.
    /// </summary>
    internal int MaxInFlight
    {
        get
        {
            lock (_lock)
            {
                return _maxInFlight;
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Waits until one more call has entered.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that returns whether a call entered within 20 seconds</returns>
    internal Task<bool> WaitForEntryAsync(CancellationToken cancellationToken)
    {
        return _entered.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }

    /// <summary>
    /// Records a call, waits for the hold and answers or fails.
    /// </summary>
    /// <typeparam name="TResult">The type of the answer</typeparam>
    /// <param name="name">The member name</param>
    /// <param name="argument">The argument</param>
    /// <param name="sequence">The entry of the call sequence</param>
    /// <param name="answer">Gives the answer</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    private async Task<TResult> RunAsync<TResult>(string name, object? argument, string sequence, Func<TResult> answer, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((name, argument, cancellationToken));
            _sequence.Add(sequence);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        _entered.Release();
        Entered?.Invoke();

        try
        {
            if (Hold is not null)
            {
                await Hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return answer();
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }

    /// <summary>
    /// Gives the result of a query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The scripted result</returns>
    private ProgramRollupResult ResultOf(ProgramRollupQuery query)
    {
        if (Answer is not null)
        {
            return Answer(query);
        }

        return Results.TryGetValue(query.Measure, out var result) ? result : new ProgramRollupResult();
    }

    #endregion // Methods

    #region IProgramRollupReader

    /// <inheritdoc/>
    public Task<ProgramRollupResult> ProgramRollupsAsync(ProgramRollupQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(ProgramRollupsAsync), query, $"Rollups {query.Measure}", () => ResultOf(query), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ProgramSourceList> ProgramSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(ProgramSourcesAsync), query, "Sources", () => Sources, cancellationToken);
    }

    #endregion // IProgramRollupReader

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _entered.Dispose();
    }

    #endregion // IDisposable
}