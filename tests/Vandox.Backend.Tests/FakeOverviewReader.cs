using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An overview reader that records its calls, answers with scripted results, can hold a call until released and can fail.
/// </summary>
internal sealed class FakeOverviewReader : IOverviewReader, IDisposable
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
    /// Gets or sets the answer of <see cref="FreshnessAsync"/>.
    /// </summary>
    internal AgentFreshness Freshness { get; set; } = new();

    /// <summary>
    /// Gets or sets the answer of <see cref="LatestValuesAsync"/>.
    /// </summary>
    internal IReadOnlyList<LatestSeries> Values { get; set; } = [];

    /// <summary>
    /// Gets or sets the answer of <see cref="LatestMariaDbStatusAsync"/>.
    /// </summary>
    internal MariaDbLatest? MariaDb { get; set; }

    /// <summary>
    /// Gets or sets the answer of <see cref="LatestServiceStatesAsync"/>.
    /// </summary>
    internal ServiceStatePage Services { get; set; } = new();

    /// <summary>
    /// Gets the recorded calls: the member name, its argument (the scan limit, the query or the bound) and its token.
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
    /// <param name="answer">The answer</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    private async Task<TResult> RunAsync<TResult>(string name, object? argument, TResult answer, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((name, argument, cancellationToken));
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        _entered.Release();

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

            return answer;
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }

    #endregion // Methods

    #region IOverviewReader

    /// <inheritdoc/>
    public Task<AgentFreshness> FreshnessAsync(int liveScanLimit, DateTimeOffset until, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(FreshnessAsync), (liveScanLimit, until), Freshness, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LatestSeries>> LatestValuesAsync(LatestValueQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(LatestValuesAsync), query, Values, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<MariaDbLatest?> LatestMariaDbStatusAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(LatestMariaDbStatusAsync), until, MariaDb, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ServiceStatePage> LatestServiceStatesAsync(LatestServiceQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(nameof(LatestServiceStatesAsync), query, Services, cancellationToken);
    }

    #endregion // IOverviewReader

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _entered.Dispose();
    }

    #endregion // IDisposable
}