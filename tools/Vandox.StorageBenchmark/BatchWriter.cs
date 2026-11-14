using System.Diagnostics.CodeAnalysis;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Collects records of one agent and writes them in batches of <see cref="BatchSize"/>.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class BatchWriter
{
    #region Constants

    /// <summary>
    /// The boot ID of the benchmark agent.
    /// </summary>
    internal const string BootId = "0123abcd-0123-0123-0123-0123456789ab";

    private const int BatchSize = 10000;
    private const string AgentId = "benchmark";

    #endregion // Constants

    #region Fields

    private readonly SqliteStore _store;

    private readonly DateTimeOffset _now;

    private readonly List<DataRecord> _records = [];

    private ulong _seq = 1;

    private long _written;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchWriter"/> class.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="now">The receive time</param>
    public BatchWriter(SqliteStore store, DateTimeOffset now)
    {
        _store = store;
        _now = now;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a record and writes the batch when it is full.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="payload">The payload</param>
    /// <param name="cancellationToken">Interrupts the write</param>
    /// <returns>A task that completes when the record is added</returns>
    public async Task AddAsync(string source, DateTimeOffset capturedAt, IPayload payload, CancellationToken cancellationToken)
    {
        _records.Add(new DataRecord
                     {
                         Origin = RecordOrigin.Agent,
                         Source = source,
                         Seq = _seq++,
                         CapturedAt = capturedAt,
                         Data = payload
                     });

        if (_records.Count >= BatchSize)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the records collected so far.
    /// </summary>
    /// <param name="cancellationToken">Interrupts the write</param>
    /// <returns>A task that returns the number of records written so far</returns>
    public async Task<long> FlushAsync(CancellationToken cancellationToken)
    {
        if (_records.Count > 0)
        {
            var result = await _store.WriteBatchAsync(new RecordBatch
                                                      {
                                                          AgentId = AgentId,
                                                          BootId = BootId,
                                                          ClockOffsetNs = 0,
                                                          ReceivedAt = _now,
                                                          Records = [.. _records]
                                                      },
                                                      cancellationToken)
                                     .ConfigureAwait(false);

            _written += result.Stored;
            _records.Clear();
        }

        return _written;
    }

    #endregion // Methods
}