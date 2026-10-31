using System.Globalization;
using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Reads the golden records of the error log fixtures (<c>testdata/logs/*.records.jsonl</c>), the records the Go collector is compared with as well.
/// </summary>
internal static class ErrorLogGolden
{
    #region Methods

    /// <summary>
    /// Reads the lines of a golden file.
    /// </summary>
    /// <param name="path">The golden file, relative to the repository root</param>
    /// <param name="cancellationToken">The token the read honors</param>
    /// <returns>The non-empty lines in file order</returns>
    internal static async Task<List<string>> ReadLinesAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(RepositoryFiles.Path(path), cancellationToken);

        return lines.Where(line => line.Length > 0).ToList();
    }

    /// <summary>
    /// Describes a golden line with the fields of the golden record, without log and event.
    /// </summary>
    /// <param name="json">The line</param>
    /// <returns>The description of <see cref="Describe"/></returns>
    internal static string DescribeLine(string json)
    {
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;
        var priority = root.GetProperty("priority").ValueKind == JsonValueKind.Null ? "-" : root.GetProperty("priority").GetInt32().ToString(CultureInfo.InvariantCulture);

        return $"{root.GetProperty("source").GetString()}|{root.GetProperty("captured_at_us").GetInt64()}|{root.GetProperty("host").GetString()}|{root.GetProperty("program").GetString()}|{root.GetProperty("pid").GetInt32()}|{priority}|{root.GetProperty("truncated").GetBoolean()}|{root.GetProperty("message").GetString()}";
    }

    /// <summary>
    /// Describes an imported record with the fields of the golden record, without log and event.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The description, equal to <see cref="DescribeLine"/> of the golden line</returns>
    internal static string Describe(DataRecord record)
    {
        var line = RecordingEmitter.Line(record);
        var priority = line.Priority is { } value ? value.ToString(CultureInfo.InvariantCulture) : "-";
        var microseconds = (record.CapturedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

        return $"{record.Source}|{microseconds}|{line.Host}|{line.Program}|{line.Pid}|{priority}|{line.Truncated}|{line.Message}";
    }

    /// <summary>
    /// Describes a golden line of the classification fixtures with every field the stages can change, except the log.
    /// </summary>
    /// <param name="json">The line</param>
    /// <returns>The description, equal to <see cref="DescribeClassified(DataRecord)"/> of the imported record</returns>
    internal static string DescribeClassified(string json)
    {
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;
        var eventName = root.TryGetProperty("event", out var property) ? property.GetString() : string.Empty;

        return $"{DescribeLine(json)}|{eventName}";
    }

    /// <summary>
    /// Describes an imported record with the fields of the golden record of the classification fixtures, the event included.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The description</returns>
    internal static string DescribeClassified(DataRecord record)
    {
        return $"{Describe(record)}|{RecordingEmitter.Line(record).Event}";
    }

    /// <summary>
    /// Sorts records by capture time, then program, then message, the order of the classification golden files.
    /// </summary>
    /// <param name="records">The records</param>
    /// <returns>The sorted records</returns>
    internal static List<DataRecord> SortedByCaptureTime(IEnumerable<DataRecord> records)
    {
        return records.OrderBy(record => record.CapturedAt)
                      .ThenBy(record => RecordingEmitter.Line(record).Program, StringComparer.Ordinal)
                      .ThenBy(record => RecordingEmitter.Line(record).Message, StringComparer.Ordinal)
                      .ToList();
    }

    /// <summary>
    /// Asserts that imported records equal the golden lines of a classification fixture: the same number, and in capture order every field except the log.
    /// </summary>
    /// <param name="imported">The imported records</param>
    /// <param name="golden">The golden lines</param>
    internal static void AssertClassifiedEqual(IEnumerable<DataRecord> imported, List<string> golden)
    {
        var sorted = SortedByCaptureTime(imported);

        Assert.HasCount(golden.Count, sorted, "one imported record per golden record");

        for (var index = 0; index < golden.Count; index++)
        {
            Assert.AreEqual(DescribeClassified(golden[index]), DescribeClassified(sorted[index]), $"record {index}");
        }
    }

    /// <summary>
    /// Asserts that the golden lines of a classification fixture carry every event of the stages and the plain priorities of the lines the stages leave alone.
    /// </summary>
    /// <param name="golden">The golden lines</param>
    internal static void AssertClassifiedCoverage(List<string> golden)
    {
        string[] expected = [
                                "dovecot.database_unavailable",
                                "dovecot.fatal",
                                "dovecot.panic",
                                "mariadb.abort",
                                "mariadb.ready",
                                "mariadb.recovery_end",
                                "mariadb.recovery_start",
                                "mariadb.shutdown",
                                "mariadb.start",
                                "postfix.database_unavailable",
                                "postfix.fatal",
                                "postfix.panic"
                            ];

        var events = golden.Select(line =>
                                   {
                                       using var document = JsonDocument.Parse(line);

                                       return document.RootElement.TryGetProperty("event", out var property) ? property.GetString()! : string.Empty;
                                   })
                           .Where(name => name.Length > 0)
                           .Distinct()
                           .Order(StringComparer.Ordinal)
                           .ToList();

        Assert.AreSequenceEqual(expected, events, "every event of the stages is in the golden records");
    }

    /// <summary>
    /// Makes the records of the golden lines as the agent sends them, numbered from a given count.
    /// </summary>
    /// <param name="lines">The golden lines</param>
    /// <param name="idOffset">The number of IDs already used</param>
    /// <returns>The records with their IDs</returns>
    internal static List<(long Id, DataRecord Record)> AgentRecords(IEnumerable<string> lines, int idOffset)
    {
        var records = new List<(long Id, DataRecord Record)>();

        foreach (var json in lines)
        {
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;
            var priority = root.GetProperty("priority");
            var record = new DataRecord
                         {
                             Origin = RecordOrigin.Agent,
                             Source = root.GetProperty("source").GetString()!,
                             CapturedAt = DateTimeOffset.UnixEpoch.AddTicks(root.GetProperty("captured_at_us").GetInt64() * 10),
                             Data = new LogLine
                                    {
                                        Log = "test.log",
                                        Host = root.GetProperty("host").GetString()!,
                                        Program = root.GetProperty("program").GetString()!,
                                        Pid = root.GetProperty("pid").GetInt32(),
                                        Priority = priority.ValueKind == JsonValueKind.Null ? null : (byte)priority.GetInt32(),
                                        Message = root.GetProperty("message").GetString()!,
                                        Truncated = root.GetProperty("truncated").GetBoolean(),
                                        Event = root.TryGetProperty("event", out var eventName) ? eventName.GetString()! : string.Empty
                                    }
                         };

            records.Add((idOffset + records.Count + 1, record));
        }

        return records;
    }

    #endregion // Methods
}