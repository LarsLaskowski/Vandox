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
                                        Truncated = root.GetProperty("truncated").GetBoolean()
                                    }
                         };

            records.Add((idOffset + records.Count + 1, record));
        }

        return records;
    }

    #endregion // Methods
}