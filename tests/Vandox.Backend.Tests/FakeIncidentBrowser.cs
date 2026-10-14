using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="IIncidentBrowser"/> with scripted results per method. It keeps a copy of every call with its token, counts the calls in flight, runs a hook
/// before each call and fails a method with a scripted exception.
/// </summary>
internal sealed class FakeIncidentBrowser : IIncidentBrowser
{
    #region Constants

    /// <summary>
    /// The name of <see cref="IIncidentBrowser.IncidentSummariesAsync"/>.
    /// </summary>
    internal const string IncidentSummaries = "IncidentSummaries";

    /// <summary>
    /// The name of <see cref="IIncidentBrowser.IncidentAsync"/>.
    /// </summary>
    internal const string Incident = "Incident";

    /// <summary>
    /// The name of <see cref="IIncidentBrowser.IncidentNotesAsync"/>.
    /// </summary>
    internal const string IncidentNotes = "IncidentNotes";

    /// <summary>
    /// The name of <see cref="IIncidentBrowser.SaveIncidentNoteAsync"/>.
    /// </summary>
    internal const string SaveIncidentNote = "SaveIncidentNote";

    #endregion // Constants

    #region Fields

    private readonly object _lock = new();

    private readonly List<FakeLogCall> _calls = [];

    private readonly List<(string Method, int Count, TaskCompletionSource Signal)> _waiters = [];

    private int _inFlight;

    private int _maxInFlight;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets a hook that runs at the start of every call, with the method name and the token; it can wait for a token or a signal.
    /// </summary>
    internal Func<string, CancellationToken, Task>? Before { get; set; }

    /// <summary>
    /// Gets the pages that the next list calls answer, one per call; when it is empty, <see cref="Summaries"/> is answered.
    /// </summary>
    internal Queue<IReadOnlyList<IncidentSummary>> SummaryPages { get; } = new();

    /// <summary>
    /// Gets or sets the answer of a list call when <see cref="SummaryPages"/> is empty.
    /// </summary>
    internal IReadOnlyList<IncidentSummary> Summaries { get; set; } = [];

    /// <summary>
    /// Gets or sets the incident that a read of one incident answers.
    /// </summary>
    internal StoredIncident? IncidentResult { get; set; }

    /// <summary>
    /// Gets or sets the notes that a read of the notes answers.
    /// </summary>
    internal IncidentNotePage NotesResult { get; set; } = new();

    /// <summary>
    /// Gets or sets the outcome that a save of a note answers.
    /// </summary>
    internal IncidentNoteOutcome SaveOutcome { get; set; } = IncidentNoteOutcome.Saved;

    /// <summary>
    /// Gets the exceptions that a method throws, by method name.
    /// </summary>
    internal Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>
    /// Gets the highest number of calls that were in flight at once.
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

    /// <summary>
    /// Gets the calls so far, in the order they started.
    /// </summary>
    internal IReadOnlyList<FakeLogCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the copies of the arguments of the calls of one method.
    /// </summary>
    /// <typeparam name="TArgument">The type of the argument</typeparam>
    /// <param name="method">The name of the method</param>
    /// <returns>The arguments in the order of the calls</returns>
    internal IReadOnlyList<TArgument> Queries<TArgument>(string method)
    {
        return Calls.Where(call => call.Method == method).Select(call => (TArgument)call.Argument!).ToArray();
    }

    /// <summary>
    /// Waits until a method was called a number of times.
    /// </summary>
    /// <param name="method">The name of the method</param>
    /// <param name="count">The number of calls</param>
    /// <returns>A task that completes when the calls were made, or fails after 20 seconds</returns>
    internal Task WaitForCallsAsync(string method, int count)
    {
        lock (_lock)
        {
            if (_calls.Count(call => call.Method == method) >= count)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _waiters.Add((method, count, signal));

            return signal.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// Records a call, runs the hook and answers or fails.
    /// </summary>
    /// <typeparam name="TResult">The type of the answer</typeparam>
    /// <param name="method">The name of the method</param>
    /// <param name="argument">The copy of the argument</param>
    /// <param name="result">Creates the answer</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    internal async Task<TResult> RunAsync<TResult>(string method, object? argument, Func<TResult> result, CancellationToken cancellationToken)
    {
        Start(new FakeLogCall(method, argument, cancellationToken));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Before is not null)
            {
                await Before(method, cancellationToken);
            }

            if (Failures.TryGetValue(method, out var failure))
            {
                throw failure;
            }

            return result();
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
    /// Adds a call to the record and wakes the waiters whose count is reached.
    /// </summary>
    /// <param name="call">The call</param>
    private void Start(FakeLogCall call)
    {
        lock (_lock)
        {
            _calls.Add(call);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);

            foreach (var waiter in _waiters.Where(item => _calls.Count(started => started.Method == item.Method) >= item.Count).ToArray())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>
    /// Takes the answer of the next list call.
    /// </summary>
    /// <returns>The page</returns>
    private IReadOnlyList<IncidentSummary> NextSummaries()
    {
        lock (_lock)
        {
            return SummaryPages.Count > 0 ? SummaryPages.Dequeue() : Summaries;
        }
    }

    #endregion // Methods

    #region IIncidentBrowser

    /// <inheritdoc />
    public Task<IReadOnlyList<IncidentSummary>> IncidentSummariesAsync(IncidentListQuery query, CancellationToken cancellationToken)
    {
        var copy = new IncidentListQuery
                   {
                       Limit = query.Limit,
                       Before = query.Before is null
                                    ? null
                                    : new IncidentPosition
                                      {
                                          StartedAt = query.Before.StartedAt,
                                          Id = query.Before.Id
                                      }
                   };

        return RunAsync(IncidentSummaries, copy, NextSummaries, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StoredIncident?> IncidentAsync(long id, CancellationToken cancellationToken)
    {
        return RunAsync(Incident, id, () => IncidentResult, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IncidentNotePage> IncidentNotesAsync(long incidentId, CancellationToken cancellationToken)
    {
        return RunAsync(IncidentNotes, incidentId, () => NotesResult, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IncidentNoteOutcome> SaveIncidentNoteAsync(IncidentNoteEdit edit, CancellationToken cancellationToken)
    {
        var copy = new IncidentNoteEdit
                   {
                       IncidentId = edit.IncidentId,
                       WrittenFor = edit.WrittenFor,
                       Text = edit.Text,
                       SavedAt = edit.SavedAt
                   };

        return RunAsync(SaveIncidentNote, copy, () => SaveOutcome, cancellationToken);
    }

    #endregion // IIncidentBrowser
}