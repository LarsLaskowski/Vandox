using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds valid records and batches for the store tests.
/// </summary>
internal static class Samples
{
    #region Constants

    /// <summary>
    /// The instant every sample is captured at, plus its offset.
    /// </summary>
    internal static readonly DateTimeOffset Base = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a metric record.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="seq">The sequence number; 0 unless the origin is agent</param>
    /// <param name="seconds">Seconds after <see cref="Base"/></param>
    /// <param name="name">The metric name</param>
    /// <returns>The record</returns>
    internal static DataRecord Metric(string origin, ulong seq, int seconds, string name)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = "host",
                   Seq = seq,
                   CapturedAt = Base.AddSeconds(seconds),
                   Data = new MetricPoint
                          {
                              Name = name,
                              Value = 1.5,
                              Unit = "percent",
                              Labels = new Dictionary<string, string>
                                       {
                                           ["cpu"] = "0",
                                           ["a"] = "b"
                                       }
                          }
               };
    }

    /// <summary>
    /// Creates a log line record.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="seq">The sequence number; 0 unless the origin is agent</param>
    /// <param name="seconds">Seconds after <see cref="Base"/></param>
    /// <param name="message">The message</param>
    /// <returns>The record</returns>
    internal static DataRecord Log(string origin, ulong seq, int seconds, string message)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = "syslog",
                   Seq = seq,
                   CapturedAt = Base.AddSeconds(seconds),
                   Data = new LogLine
                          {
                              Log = "syslog",
                              Host = "web-1",
                              Program = "sshd",
                              Pid = 12,
                              Priority = 3,
                              Message = message,
                              Truncated = true
                          }
               };
    }

    /// <summary>
    /// Creates a gap record, which is stored as a JSON document.
    /// </summary>
    /// <param name="seconds">Seconds after <see cref="Base"/></param>
    /// <returns>The record</returns>
    internal static DataRecord Gap(int seconds)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = "backend",
                   CapturedAt = Base.AddSeconds(seconds),
                   Data = new Gap
                          {
                              From = Base,
                              To = Base.AddMinutes(1),
                              Cause = GapCause.NoData
                          }
               };
    }

    /// <summary>
    /// Creates a batch received one minute after <see cref="Base"/>.
    /// </summary>
    /// <param name="records">The records</param>
    /// <returns>The batch</returns>
    internal static RecordBatch Batch(params DataRecord[] records)
    {
        return new RecordBatch
               {
                   ReceivedAt = Base.AddMinutes(1),
                   Records = records
               };
    }

    /// <summary>
    /// Creates an agent batch with an agent ID, a boot ID and a clock offset.
    /// </summary>
    /// <param name="records">The records</param>
    /// <returns>The batch</returns>
    internal static RecordBatch AgentBatch(params DataRecord[] records)
    {
        var batch = Batch(records);

        batch.AgentId = "agent-1";
        batch.BootId = "0123abcd-0123-0123-0123-0123456789ab";
        batch.ClockOffsetNs = -5;

        return batch;
    }

    /// <summary>
    /// Creates a query of one kind over a day around <see cref="Base"/>.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The query</returns>
    internal static RecordQuery Query(string kind)
    {
        return new RecordQuery
               {
                   Kind = kind,
                   From = Base.AddHours(-1),
                   To = Base.AddHours(1),
                   Limit = 100
               };
    }

    /// <summary>
    /// Creates the start of an import.
    /// </summary>
    /// <param name="seed">A byte that makes the content hash unique</param>
    /// <returns>The start</returns>
    internal static ImportFileStart ImportStart(byte seed)
    {
        return new ImportFileStart
               {
                   Sha256 = Enumerable.Repeat(seed, 32).ToArray(),
                   Size = 100,
                   Name = "logs/syslog.gz",
                   FileName = "syslog",
                   ModTime = Base,
                   SourceType = "syslog",
                   StartedAt = Base
               };
    }

    #endregion // Methods
}