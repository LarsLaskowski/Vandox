using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds the stamps, records and results the history tests read.
/// </summary>
internal static class HistorySamples
{
    #region Constants

    /// <summary>
    /// The instant the samples are measured from.
    /// </summary>
    internal static readonly DateTimeOffset Base = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a stamp.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="kind">The record kind</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The stamp</returns>
    internal static SnapshotStamp Stamp(long id, string kind, DateTimeOffset capturedAt)
    {
        return new SnapshotStamp
               {
                   Id = id,
                   Kind = kind,
                   CapturedAt = capturedAt
               };
    }

    /// <summary>
    /// Creates a stored record of a payload.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="payload">The payload</param>
    /// <returns>The stored record</returns>
    internal static StoredRecord Record(long id, string source, DateTimeOffset capturedAt, IPayload payload)
    {
        return new StoredRecord
               {
                   Id = id,
                   Record = new DataRecord
                            {
                                Origin = RecordOrigin.Backend,
                                Source = source,
                                CapturedAt = capturedAt,
                                Data = payload
                            }
               };
    }

    /// <summary>
    /// Creates a stored process snapshot with one program.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The stored record</returns>
    internal static StoredRecord ProcessRecord(long id, string source, DateTimeOffset capturedAt)
    {
        return Record(id,
                      source,
                      capturedAt,
                      new ProcessSnapshot
                      {
                          Complete = true,
                          Programs = [Program("nginx", 1, 1.0, 4096)]
                      });
    }

    /// <summary>
    /// Creates a stored connection snapshot with one state.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The stored record</returns>
    internal static StoredRecord ConnectionRecord(long id, string source, DateTimeOffset capturedAt)
    {
        return Record(id,
                      source,
                      capturedAt,
                      new ConnectionSnapshot
                      {
                          Complete = true,
                          States = [State("tcp", "ESTABLISHED", 3)]
                      });
    }

    /// <summary>
    /// Creates a program aggregate.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="count">The number of processes</param>
    /// <param name="cpu">The CPU percent</param>
    /// <param name="rss">The resident bytes</param>
    /// <returns>The aggregate</returns>
    internal static ProgramAggregate Program(string program, uint count, double cpu, ulong rss)
    {
        return new ProgramAggregate
               {
                   Program = program,
                   Count = count,
                   CpuPercent = cpu,
                   RssBytes = rss
               };
    }

    /// <summary>
    /// Creates a state count.
    /// </summary>
    /// <param name="proto">The protocol</param>
    /// <param name="state">The state</param>
    /// <param name="count">The count</param>
    /// <returns>The state count</returns>
    internal static StateCount State(string proto, string state, uint count)
    {
        return new StateCount
               {
                   Proto = proto,
                   State = state,
                   Count = count
               };
    }

    /// <summary>
    /// Joins the text of runs.
    /// </summary>
    /// <param name="runs">The runs</param>
    /// <returns>The text</returns>
    internal static string Text(IReadOnlyList<Logs.TextRun> runs)
    {
        return string.Concat(runs.Select(run => run.Text));
    }

    #endregion // Methods
}