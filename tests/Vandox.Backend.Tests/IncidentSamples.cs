using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds log line, snapshot and metric records and writes them through the real store for the incident tests.
/// </summary>
internal static class IncidentSamples
{
    #region Constants

    /// <summary>
    /// The source of the records that the legacy monitoring log parser writes.
    /// </summary>
    internal const string Legacy = "legacy-monitor";

    /// <summary>
    /// The time of the first kill that the incident tests use.
    /// </summary>
    internal static readonly DateTimeOffset Base = new(2026, 10, 2, 4, 59, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a log line record of an imported syslog.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="source">The source of the record</param>
    /// <param name="log">The log the line came from</param>
    /// <returns>The record</returns>
    internal static DataRecord Line(DateTimeOffset at, string program, string message, string source = "syslog", string log = "syslog")
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = source,
                   CapturedAt = at,
                   Data = new LogLine
                          {
                              Log = log,
                              Host = "web-1",
                              Program = program,
                              Message = message
                          }
               };
    }

    /// <summary>
    /// Creates the line of the kernel that reports an OOM kill.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="source">The source of the record; the same kill in another source is a copy</param>
    /// <param name="log">The log the line came from</param>
    /// <param name="pid">The victim's process ID</param>
    /// <param name="command">The victim's command</param>
    /// <returns>The record</returns>
    internal static DataRecord Kill(DateTimeOffset at, string source = "syslog", string log = "syslog", int pid = 4242, string command = "mariadbd")
    {
        return Line(at, "kernel", $"Out of memory: Killed process {pid} ({command}) total-vm:8400000kB, anon-rss:7400000kB, file-rss:0kB, shmem-rss:0kB", source, log);
    }

    /// <summary>
    /// Creates the first line of a boot.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord Boot(DateTimeOffset at)
    {
        return Line(at, "kernel", "Linux version 5.15.0-91-generic (buildd@lcy02) #101-Ubuntu SMP");
    }

    /// <summary>
    /// Creates a record that holds a process snapshot of the legacy monitoring log.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="programs">The programs with their resident memory in bytes</param>
    /// <returns>The record</returns>
    internal static DataRecord Processes(DateTimeOffset at, params (string Program, ulong Rss)[] programs)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = Legacy,
                   CapturedAt = at,
                   Data = new ProcessSnapshot
                          {
                              Complete = true,
                              Programs = programs.Select(entry => new ProgramAggregate
                                                                  {
                                                                      Program = entry.Program,
                                                                      Count = 1,
                                                                      RssBytes = entry.Rss
                                                                  })
                                                 .ToList()
                          }
               };
    }

    /// <summary>
    /// Creates a record that holds a connection snapshot of the legacy monitoring log.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="established">The number of established TCP sockets</param>
    /// <param name="command">The command of the one process that holds them</param>
    /// <returns>The record</returns>
    internal static DataRecord Connections(DateTimeOffset at, uint established, string command)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = Legacy,
                   CapturedAt = at,
                   Data = new ConnectionSnapshot
                          {
                              Complete = true,
                              States = [
                                           new StateCount
                                           {
                                               Proto = Proto.Tcp,
                                               State = "ESTABLISHED",
                                               Count = established
                                           }
                                       ],
                              Processes = [
                                              new ProcessConnections
                                              {
                                                  Pid = 1,
                                                  Command = command,
                                                  Count = established
                                              }
                                          ]
                          }
               };
    }

    /// <summary>
    /// Creates a record that holds an unlabeled metric of the legacy monitoring log.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="name">The metric name</param>
    /// <param name="value">The value</param>
    /// <returns>The record</returns>
    internal static DataRecord Metric(DateTimeOffset at, string name, double value)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = Legacy,
                   CapturedAt = at,
                   Data = new MetricPoint
                          {
                              Name = name,
                              Value = value
                          }
               };
    }

    /// <summary>
    /// Writes records to the store in one batch.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="cancellationToken">The token that cancels the write</param>
    /// <param name="records">The records</param>
    /// <returns>A task that completes when the records are stored</returns>
    internal static async Task WriteAsync(SqliteStore store, CancellationToken cancellationToken, params DataRecord[] records)
    {
        var result = await store.WriteBatchAsync(new RecordBatch
                                                 {
                                                     ReceivedAt = Base.AddDays(1),
                                                     Records = records
                                                 },
                                                 cancellationToken);

        Assert.AreEqual(records.Length, result.Stored, "all records of the arranged batch are stored");
    }

    #endregion // Methods
}