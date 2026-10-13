using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="ILogExplorer"/> with scripted results. It keeps a copy of every query with its token, counts the calls in flight, and can wait inside a call
/// (<see cref="Before"/>) or fail.
/// </summary>
internal sealed class FakeLogExplorer : ILogExplorer
{
    #region Constants

    /// <summary>
    /// The name of <see cref="FindLogsAsync"/> in <see cref="Calls"/>.
    /// </summary>
    internal const string FindLogs = "FindLogs";

    /// <summary>
    /// The name of <see cref="LogContextAsync"/> in <see cref="Calls"/>.
    /// </summary>
    internal const string LogContext = "LogContext";

    /// <summary>
    /// The name of <see cref="LogSourcesAsync"/> in <see cref="Calls"/>.
    /// </summary>
    internal const string LogSources = "LogSources";

    /// <summary>
    /// The name of <see cref="TemplateAsync"/> in <see cref="Calls"/>.
    /// </summary>
    internal const string Template = "Template";

    /// <summary>
    /// The name of <see cref="TemplatesAsync"/> in <see cref="Calls"/>.
    /// </summary>
    internal const string Templates = "Templates";

    /// <summary>
    /// The name of <see cref="SourceHoursAsync"/> in <see cref="Calls"/>.
    /// </summary>
    internal const string SourceHours = "SourceHours";

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
    /// Gets or sets a function that runs inside every call, after the call is recorded: the method name and the token of the call. It can wait for a signal, for the token
    /// or for nothing.
    /// </summary>
    internal Func<string, CancellationToken, Task>? Before { get; set; }

    /// <summary>
    /// Gets the pages that <see cref="FindLogsAsync"/> returns, one per call; <see cref="FindPage"/> when none is left.
    /// </summary>
    internal Queue<LogPage> FindPages { get; } = new();

    /// <summary>
    /// Gets or sets the page that <see cref="FindLogsAsync"/> returns when <see cref="FindPages"/> is empty.
    /// </summary>
    internal LogPage FindPage { get; set; } = new();

    /// <summary>
    /// Gets or sets the result of <see cref="LogContextAsync"/>.
    /// </summary>
    internal LogContext ContextResult { get; set; } = new();

    /// <summary>
    /// Gets or sets the result of <see cref="LogSourcesAsync"/>.
    /// </summary>
    internal IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets the result of <see cref="TemplateAsync"/>.
    /// </summary>
    internal LogTemplate? TemplateResult { get; set; }

    /// <summary>
    /// Gets or sets the result of <see cref="TemplatesAsync"/>.
    /// </summary>
    internal IReadOnlyList<TemplateCount> TemplateList { get; set; } = [];

    /// <summary>
    /// Gets or sets the result of <see cref="SourceHoursAsync"/>.
    /// </summary>
    internal IReadOnlyList<DateTimeOffset> Hours { get; set; } = [];

    /// <summary>
    /// Gets the exceptions that a method throws (after <see cref="Before"/>), by method name.
    /// </summary>
    internal Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>
    /// Gets the highest number of calls that were in flight at the same time.
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
    /// Gets a snapshot of the calls in the order they started.
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
    /// Gets the copies of the queries of one method.
    /// </summary>
    /// <typeparam name="TQuery">The type of the query</typeparam>
    /// <param name="method">The name of the method</param>
    /// <returns>The queries in call order</returns>
    internal IReadOnlyList<TQuery> Queries<TQuery>(string method)
    {
        return Calls.Where(call => call.Method == method).Select(call => (TQuery)call.Argument!).ToArray();
    }

    /// <summary>
    /// Waits until at least a number of calls of a method have started.
    /// </summary>
    /// <param name="method">The name of the method</param>
    /// <param name="count">The number of calls</param>
    /// <returns>A task that completes when the calls have started</returns>
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
    /// Records a call, counts it as in flight while it runs, and returns the scripted result. Used by the methods of this fake and by doubles that share its counters.
    /// </summary>
    /// <typeparam name="TResult">The type of the result</typeparam>
    /// <param name="method">The name of the method</param>
    /// <param name="argument">A copy of the query, or <c>null</c></param>
    /// <param name="result">Returns the scripted result</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the result</returns>
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
    /// Records the start of a call and wakes the waiters.
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
    /// Takes the next scripted page.
    /// </summary>
    /// <returns>The page</returns>
    private LogPage NextPage()
    {
        lock (_lock)
        {
            return FindPages.Count > 0 ? FindPages.Dequeue() : FindPage;
        }
    }

    #endregion // Methods

    #region ILogExplorer

    /// <inheritdoc />
    public Task<LogPage> FindLogsAsync(LogFilter filter, CancellationToken cancellationToken)
    {
        var copy = new LogFilter
                   {
                       Text = filter.Text,
                       Source = filter.Source,
                       Program = filter.Program,
                       MaxPriority = filter.MaxPriority,
                       TemplateId = filter.TemplateId,
                       From = filter.From,
                       To = filter.To,
                       After = filter.After is null
                                   ? null
                                   : new LogPosition
                                     {
                                         CapturedAt = filter.After.CapturedAt,
                                         Id = filter.After.Id
                                     },
                       Limit = filter.Limit,
                       ScanLimit = filter.ScanLimit
                   };

        return RunAsync(FindLogs, copy, NextPage, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LogContext> LogContextAsync(LogContextQuery query, CancellationToken cancellationToken)
    {
        var copy = new LogContextQuery
                   {
                       RecordId = query.RecordId,
                       Before = query.Before,
                       After = query.After
                   };

        return RunAsync(LogContext, copy, () => ContextResult, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> LogSourcesAsync(CancellationToken cancellationToken)
    {
        return RunAsync(LogSources, null, () => Sources, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LogTemplate?> TemplateAsync(long id, CancellationToken cancellationToken)
    {
        return RunAsync(Template, id, () => TemplateResult, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateCount>> TemplatesAsync(TemplateListQuery query, CancellationToken cancellationToken)
    {
        var copy = new TemplateListQuery
                   {
                       Source = query.Source,
                       Program = query.Program,
                       From = query.From,
                       To = query.To,
                       Limit = query.Limit
                   };

        return RunAsync(Templates, copy, () => TemplateList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DateTimeOffset>> SourceHoursAsync(SourceHourQuery query, CancellationToken cancellationToken)
    {
        var copy = new SourceHourQuery
                   {
                       Source = query.Source,
                       From = query.From,
                       To = query.To,
                       Limit = query.Limit
                   };

        return RunAsync(SourceHours, copy, () => Hours, cancellationToken);
    }

    #endregion // ILogExplorer
}