using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Writes records as one agent sends them: origin <c>agent</c>, numbered on from the previous batch.
/// </summary>
internal sealed class AgentRecords
{
    #region Constants

    /// <summary>
    /// The agent ID of the batches.
    /// </summary>
    internal const string AgentId = "web-1";

    #endregion // Constants

    #region Fields

    private ulong _seq;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Creates a kernel event of an OOM kill.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord OomKill(DateTimeOffset at)
    {
        return new DataRecord
               {
                   Source = "kernel",
                   CapturedAt = at,
                   Data = new KernelEvent
                          {
                              Type = KernelEvent.TypeOomKill,
                              OomKill = new OomKill
                                        {
                                            VictimPid = 4242,
                                            VictimCommand = "mariadbd"
                                        }
                          }
               };
    }

    /// <summary>
    /// Creates a kernel event of a boot.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord BootEvent(DateTimeOffset at)
    {
        return new DataRecord
               {
                   Source = "kernel",
                   CapturedAt = at,
                   Data = new KernelEvent
                          {
                              Type = KernelEvent.TypeBoot,
                              Boot = new Boot
                                     {
                                         BootId = "0123abcd-0123-0123-0123-0123456789ab"
                                     }
                          }
               };
    }

    /// <summary>
    /// Creates a journal line of the agent that the catalog reads as an OOM kill.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="pid">The victim's process ID</param>
    /// <returns>The record</returns>
    internal static DataRecord KillLine(DateTimeOffset at, int pid = 4242)
    {
        return IncidentSamples.Kill(at, "journal", "journal", pid);
    }

    /// <summary>
    /// Writes records as one batch of the agent: each gets origin <c>agent</c> and the next sequence number.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="receivedAt">The receive time of the batch</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <param name="records">The records</param>
    /// <returns>A task that completes when the batch is stored</returns>
    internal async Task WriteAsync(SqliteStore store, DateTimeOffset receivedAt, CancellationToken cancellationToken, params DataRecord[] records)
    {
        foreach (var record in records)
        {
            _seq++;
            record.Origin = RecordOrigin.Agent;
            record.Seq = _seq;
        }

        var result = await store.WriteBatchAsync(ReportSamples.AgentBatch(AgentId, receivedAt, records), cancellationToken);

        Assert.AreEqual(records.Length, result.Stored, "all records of the arranged agent batch are stored");
    }

    #endregion // Methods
}