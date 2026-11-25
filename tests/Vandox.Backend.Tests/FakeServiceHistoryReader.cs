using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A service history reader that answers from a scripted result, records its calls in order, can hold a call until released and can fail.
/// </summary>
internal sealed class FakeServiceHistoryReader : IServiceHistoryReader, IDisposable
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
    /// Gets or sets an action that runs after each call entered, or <c>null</c> for none.
    /// </summary>
    internal Action? Entered { get; set; }

    /// <summary>
    /// Gets or sets the answer of <see cref="ServiceAvailabilityAsync"/>.
    /// </summary>
    internal ServiceAvailabilityResult Result { get; set; } = new();

    /// <summary>
    /// Gets or sets a function that gives the answer of a query instead of <see cref="Result"/>, or <c>null</c> for none.
    /// </summary>
    internal Func<ServiceAvailabilityQuery, ServiceAvailabilityResult>? AnswerResult { get; set; }

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
    /// Gets the queries of the calls in order.
    /// </summary>
    internal IReadOnlyList<ServiceAvailabilityQuery> Queries => [.. Calls.Select(call => (ServiceAvailabilityQuery)call.Argument!)];

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

    #endregion // Methods

    #region IServiceHistoryReader

    /// <inheritdoc/>
    public async Task<ServiceAvailabilityResult> ServiceAvailabilityAsync(ServiceAvailabilityQuery query, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((nameof(ServiceAvailabilityAsync), query, cancellationToken));
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

            return AnswerResult is null ? Result : AnswerResult(query);
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }

    #endregion // IServiceHistoryReader

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _entered.Dispose();
    }

    #endregion // IDisposable
}