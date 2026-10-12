using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Signatures;

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
        return LogWithEvent(origin, seq, seconds, message, string.Empty);
    }

    /// <summary>
    /// Creates a log line record that carries an event.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="seq">The sequence number; 0 unless the origin is agent</param>
    /// <param name="seconds">Seconds after <see cref="Base"/></param>
    /// <param name="message">The message</param>
    /// <param name="eventName">The event, or an empty text for none</param>
    /// <returns>The record</returns>
    internal static DataRecord LogWithEvent(string origin, ulong seq, int seconds, string message, string eventName)
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
                              Truncated = true,
                              Event = eventName
                          }
               };
    }

    /// <summary>
    /// Creates a log line record with its own source, program and capture time.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="seq">The sequence number; 0 unless the origin is agent</param>
    /// <param name="source">The source of the record</param>
    /// <param name="program">The program</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="message">The message</param>
    /// <returns>The record</returns>
    internal static DataRecord LogAt(string origin, ulong seq, string source, string program, DateTimeOffset capturedAt, string message)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = source,
                   Seq = seq,
                   CapturedAt = capturedAt,
                   Data = new LogLine
                          {
                              Log = source,
                              Host = "web-1",
                              Program = program,
                              Message = message
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

    /// <summary>
    /// Builds the largest incident the builder can produce from texts that the JSON encoder writes with six bytes per character.
    /// </summary>
    /// <returns>The incident</returns>
    internal static Incident WorstCase()
    {
        var text = new string('<', 1024);
        var kill = Base.AddHours(1);
        var cluster = new KillCluster
                      {
                          Start = kill.AddHours(-1),
                          End = kill.AddMinutes(45),
                          FirstKillAt = kill,
                          LastKillAt = kill.AddMinutes(15),
                          KillCount = 20,
                          Kills = Enumerable.Range(0, 16)
                                            .Select(index => new IncidentKill
                                                             {
                                                                 At = kill.AddSeconds(index),
                                                                 Host = text,
                                                                 VictimPid = long.MaxValue,
                                                                 VictimCommand = text,
                                                                 AnonRssBytes = long.MaxValue,
                                                                 OomScoreAdj = long.MinValue,
                                                                 RecordIds = Enumerable.Repeat(long.MaxValue, 16).ToList(),
                                                                 LineCount = long.MaxValue
                                                             })
                                            .ToList()
                      };
        var builder = new IncidentBuilder(cluster, TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        for (var index = 0; index < 1100; index++)
        {
            builder.AddEvent(HeavyEvent(index == 0 ? IncidentSignatures.Boot : IncidentSignatures.ServiceStart, kill.AddMinutes(-59).AddSeconds(index), text));
        }

        builder.AddProcessSnapshot(1, kill.AddMinutes(-30), HeavyPrograms(0));
        builder.AddProcessSnapshot(2, kill.AddMinutes(-10), HeavyPrograms(1));
        builder.AddConnectionSnapshot(3, kill.AddMinutes(-20), HeavyConnections(text));
        AddHeavyMetrics(builder, kill);
        AddHeavyTemplates(builder, kill, text);

        return builder.Build();
    }

    /// <summary>
    /// Creates a detected event whose texts, host and record IDs are at their caps.
    /// </summary>
    /// <param name="signatureId">The signature ID</param>
    /// <param name="at">The time</param>
    /// <param name="text">The text of 1,024 bytes</param>
    /// <returns>The event</returns>
    private static DetectedEvent HeavyEvent(string signatureId, DateTimeOffset at, string text)
    {
        return new DetectedEvent
               {
                   SignatureId = signatureId,
                   CapturedAt = at,
                   Host = text,
                   Texts = new Dictionary<string, string>
                           {
                               ["unit"] = text,
                               ["description"] = text
                           },
                   RecordIds = Enumerable.Repeat(long.MaxValue, 256).ToList(),
                   LineCount = long.MaxValue
               };
    }

    /// <summary>
    /// Creates a process snapshot of 4,096 programs with names of 1,024 bytes of <c>&lt;</c> and a backup program.
    /// </summary>
    /// <param name="step">0 for the first snapshot, 1 for the later one in which every program grew</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot HeavyPrograms(int step)
    {
        var programs = Enumerable.Range(0, 4096)
                                 .Select(index => new ProgramAggregate
                                                  {
                                                      Program = new string('<', 1018) + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                                                      Count = 1,
                                                      RssBytes = (ulong)(1000 + index + (step * ((index % 7) + 1)))
                                                  })
                                 .ToList();

        programs.Add(new ProgramAggregate
                     {
                         Program = "pzstd",
                         Count = 1,
                         RssBytes = 200
                     });

        return new ProcessSnapshot
               {
                   Complete = true,
                   Programs = programs
               };
    }

    /// <summary>
    /// Creates a connection snapshot with three processes whose commands are at their cap.
    /// </summary>
    /// <param name="text">The text of 1,024 bytes</param>
    /// <returns>The snapshot</returns>
    private static ConnectionSnapshot HeavyConnections(string text)
    {
        return new ConnectionSnapshot
               {
                   Complete = true,
                   States = [
                                new StateCount
                                {
                                    Proto = Proto.Tcp,
                                    State = "ESTABLISHED",
                                    Count = 5
                                }
                            ],
                   Processes = [
                                   new ProcessConnections
                                   {
                                       Pid = 1,
                                       Command = text,
                                       Count = 3
                                   },
                                   new ProcessConnections
                                   {
                                       Pid = 2,
                                       Command = text + "a",
                                       Count = 2
                                   },
                                   new ProcessConnections
                                   {
                                       Pid = 3,
                                       Command = text + "b",
                                       Count = 1
                                   }
                               ]
               };
    }

    /// <summary>
    /// Adds a sample of each metric definition, with a source of 128 bytes of <c>&lt;</c>.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="kill">The time of the first kill</param>
    private static void AddHeavyMetrics(IncidentBuilder builder, DateTimeOffset kill)
    {
        var source = new string('<', 128);
        var memory = new Dictionary<string, string>
                     {
                         ["resource"] = "memory",
                         ["kind"] = "some"
                     };
        var full = new Dictionary<string, string>
                   {
                       ["resource"] = "memory",
                       ["kind"] = "full"
                   };

        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "mem_available_bytes",
                              Value = double.MaxValue
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "swap_used_bytes",
                              Value = double.MaxValue
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "pressure_avg10",
                              Value = double.MaxValue,
                              Labels = memory
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "pressure_avg10",
                              Value = double.MaxValue,
                              Labels = full
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "load1",
                              Value = double.MaxValue
                          });
    }

    /// <summary>
    /// Adds 25 new templates and 25 spikes with texts at their caps.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="kill">The time of the first kill</param>
    /// <param name="text">The text of 1,024 bytes</param>
    private static void AddHeavyTemplates(IncidentBuilder builder, DateTimeOffset kill, string text)
    {
        var source = new string('<', 128);

        for (var index = 0; index < 25; index++)
        {
            builder.AddNewTemplate(new IncidentNewTemplate
                                   {
                                       Source = source,
                                       Program = text,
                                       Template = text,
                                       FirstSeen = kill,
                                       Count = long.MaxValue
                                   });
            builder.AddTemplateSpike(new IncidentTemplateSpike
                                     {
                                         Source = source,
                                         Program = text,
                                         Template = text,
                                         Hour = kill,
                                         Count = long.MaxValue,
                                         UsualLevel = double.MaxValue
                                     });
        }
    }

    #endregion // Methods
}