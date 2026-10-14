using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="IRecordReader"/> over records that a test adds: it answers a query the way the store does (kind, source, name of a metric, the range
/// <c>[From, To)</c>, the keyset position <see cref="RecordQuery.AfterId"/>, order by time and number, limit), so a page query that drops the source or the
/// name returns the records of other series. It keeps a copy of every query and its token, counts the calls in flight and runs a hook before every call.
/// </summary>
internal sealed class ScriptedRecordReader : IRecordReader
{
    #region Fields

    private readonly object _lock = new();

    private readonly List<StoredRecord> _records = [];

    private readonly List<(RecordQuery Query, CancellationToken CancellationToken)> _queries = [];

    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];

    private long _nextId = 1;

    private int _inFlight;

    private int _maxInFlight;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets a hook that runs at the start of every call; it can wait for a token or a signal.
    /// </summary>
    internal Func<RecordQuery, CancellationToken, Task>? Before { get; set; }

    /// <summary>
    /// Gets or sets an exception that every call throws.
    /// </summary>
    internal Exception? Failure { get; set; }

    /// <summary>
    /// Gets the copies of the queries so far, in the order they started.
    /// </summary>
    internal IReadOnlyList<RecordQuery> Queries
    {
        get
        {
            lock (_lock)
            {
                return _queries.Select(entry => entry.Query).ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the tokens of the calls so far, in the order they started.
    /// </summary>
    internal IReadOnlyList<CancellationToken> Tokens
    {
        get
        {
            lock (_lock)
            {
                return _queries.Select(entry => entry.CancellationToken).ToArray();
            }
        }
    }

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
    /// Gets the number of records that were added.
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _records.Count;
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a process snapshot; the records are numbered in the order they are added, so add them in the order of their times.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="at">The capture time</param>
    /// <param name="programs">The programs with their resident memory in bytes</param>
    /// <returns>The number of the record</returns>
    internal long AddProcesses(string source, DateTimeOffset at, params (string Program, ulong Rss)[] programs)
    {
        return Add(source,
                   at,
                   new ProcessSnapshot
                   {
                       Complete = true,
                       Programs = programs.Select(entry => new ProgramAggregate
                                                           {
                                                               Program = entry.Program,
                                                               Count = 1,
                                                               RssBytes = entry.Rss
                                                           })
                                          .ToList()
                   });
    }

    /// <summary>
    /// Adds a connection snapshot.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="at">The capture time</param>
    /// <param name="counts">The sockets of each state</param>
    /// <returns>The number of the record</returns>
    internal long AddConnections(string source, DateTimeOffset at, params uint[] counts)
    {
        return Add(source,
                   at,
                   new ConnectionSnapshot
                   {
                       Complete = true,
                       States = counts.Select((count, index) => new StateCount
                                                                {
                                                                    Proto = Proto.Tcp,
                                                                    State = $"STATE{index}",
                                                                    Count = count
                                                                })
                                      .ToList()
                   });
    }

    /// <summary>
    /// Adds a metric point.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="at">The capture time</param>
    /// <param name="name">The name of the metric</param>
    /// <param name="value">The value</param>
    /// <returns>The number of the record</returns>
    internal long AddMetric(string source, DateTimeOffset at, string name, double value)
    {
        return Add(source,
                   at,
                   new MetricPoint
                   {
                       Name = name,
                       Value = value
                   });
    }

    /// <summary>
    /// Waits until a number of queries were made.
    /// </summary>
    /// <param name="count">The number of queries</param>
    /// <returns>A task that completes when the queries were made, or fails after 20 seconds</returns>
    internal Task WaitForCallsAsync(int count)
    {
        lock (_lock)
        {
            if (_queries.Count >= count)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _waiters.Add((count, signal));

            return signal.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// Adds a record.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="at">The capture time</param>
    /// <param name="payload">The payload</param>
    /// <returns>The number of the record</returns>
    private long Add(string source, DateTimeOffset at, IPayload payload)
    {
        lock (_lock)
        {
            var id = _nextId++;

            _records.Add(new StoredRecord
                         {
                             Id = id,
                             Record = new DataRecord
                                      {
                                          Origin = RecordOrigin.Import,
                                          Source = source,
                                          CapturedAt = at,
                                          Data = payload
                                      }
                         });

            return id;
        }
    }

    /// <summary>
    /// Answers a query from the records.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The page</returns>
    private StoredRecord[] Answer(RecordQuery query)
    {
        lock (_lock)
        {
            return _records.Where(stored => stored.Record.Kind == query.Kind)
                           .Where(stored => query.Source.Length == 0 || stored.Record.Source == query.Source)
                           .Where(stored => query.Name.Length == 0 || (stored.Record.Data is MetricPoint point && point.Name == query.Name))
                           .Where(stored => stored.Record.CapturedAt >= query.From && stored.Record.CapturedAt < query.To)
                           .Where(stored => query.AfterId == 0 || stored.Record.CapturedAt > query.From || stored.Id > query.AfterId)
                           .OrderBy(stored => stored.Record.CapturedAt)
                           .ThenBy(stored => stored.Id)
                           .Take(query.Limit)
                           .ToArray();
        }
    }

    #endregion // Methods

    #region IRecordReader

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredRecord>> RecordsAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var copy = new RecordQuery
                   {
                       Kind = query.Kind,
                       Source = query.Source,
                       Name = query.Name,
                       From = query.From,
                       To = query.To,
                       Limit = query.Limit,
                       AfterId = query.AfterId
                   };

        lock (_lock)
        {
            _queries.Add((copy, cancellationToken));
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);

            foreach (var waiter in _waiters.Where(item => _queries.Count >= item.Count).ToArray())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Before is not null)
            {
                await Before(copy, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Answer(query);
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }

    #endregion // IRecordReader
}