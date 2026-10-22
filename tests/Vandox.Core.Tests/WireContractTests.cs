using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Vandox.Core.Model;
using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Contract tests that decode the plaintext batch the Go agent's encoder writes (<c>testdata/wire/all-kinds.jsonl</c>).
/// </summary>
[TestClass]
public class WireContractTests
{
    #region Fields

    /// <summary>
    /// The shared cases whose line under test holds a JSON escape that the fixture spells with a doubled backslash.
    /// </summary>
    private static readonly string[] _escapedCaseIds = ["K4", "K5", "N4", "N7", "N8", "N9", "N10", "N11", "D9", "S13", "S14", "S15", "S16", "S17", "S18", "S19", "S20", "S21", "S22", "S23", "S24", "S25", "S26", "S27", "S28", "S29", "S30", "S31"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Lists the names of the shared decoder cases, one data row per case.
    /// </summary>
    /// <returns>The data rows</returns>
    public static IEnumerable<object[]> SharedCaseNames()
    {
        return SharedCases().Select(candidate => new object[] { candidate.GetProperty("name").GetString() ?? string.Empty });
    }

    /// <summary>
    /// Names a data row of a shared decoder case after the case.
    /// </summary>
    /// <param name="method">The test method</param>
    /// <param name="data">The data of the row</param>
    /// <returns>The display name</returns>
    public static string SharedCaseDisplayName(MethodInfo method, object[] data)
    {
        return $"{method.Name} {data[0]}";
    }

    /// <summary>
    /// Every record kind of the Go encoder's golden batch is accepted by the decoder.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DecodeGoldenBatchAcceptsEveryKind()
    {
        // Arrange
        var plain = await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/wire/all-kinds.jsonl"), CancellationToken.None);
        using var compressed = new MemoryStream();

        await using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
        {
            await gzip.WriteAsync(plain, CancellationToken.None);
        }

        compressed.Position = 0;

        // Act
        var kinds = new List<string>();
        MariaDbStatus? refused = null;
        string agentId;

        using (var decoder = await BatchDecoder.OpenAsync(compressed, null, CancellationToken.None))
        {
            agentId = decoder.Header.AgentId;

            for (var record = await decoder.NextAsync(CancellationToken.None); record is not null; record = await decoder.NextAsync(CancellationToken.None))
            {
                kinds.Add(record.Kind);

                if (record.Data is MariaDbStatus { ErrorCode: not null } status)
                {
                    refused = status;
                }
            }
        }

        // Assert
        Assert.AreEqual("agent-1", agentId, "agent id");

        var expected = new[] { RecordKind.Metric, RecordKind.ProcessSnapshot, RecordKind.ConnectionSnapshot, RecordKind.ServiceState, RecordKind.MariaDbStatus, RecordKind.KernelEvent, RecordKind.KernelEvent, RecordKind.LogLine, RecordKind.Gap, RecordKind.ConnectionSnapshot, RecordKind.ConnectionSnapshot, RecordKind.MariaDbStatus, RecordKind.ProcessSnapshot, RecordKind.ConnectionSnapshot, RecordKind.BackupRun, RecordKind.BackupRun, RecordKind.KernelEvent };

        Assert.AreSequenceEqual(expected, kinds, "kinds in order");
        Assert.IsNotNull(refused, "the golden batch holds a MariaDB status of a database that refused the login");
        Assert.AreEqual(MariaDbStatus.NotAnswering, refused.Availability, "availability of the refused login");
        Assert.AreEqual((ushort)1045, refused.ErrorCode, "error code of the refused login");
        Assert.IsNull(refused.Validate(), "the refused login is valid");
    }

    /// <summary>
    /// The golden batch holds the kill identity, the triggered snapshots and the backup run, and the decoder reads every value the Go encoder wrote.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WireContractDecodesGoldenBatch()
    {
        // Arrange
        var plain = await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/wire/all-kinds.jsonl"), CancellationToken.None);
        var records = new List<DataRecord>();

        using (var compressed = GzipOf(plain))
            using (var decoder = await BatchDecoder.OpenAsync(compressed, null, CancellationToken.None))
            {
                for (var record = await decoder.NextAsync(CancellationToken.None); record is not null; record = await decoder.NextAsync(CancellationToken.None))
                {
                    records.Add(record);
                }
            }

        const string bootId = "0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f";
        const string previousBootId = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";

        // Act
        var kill = records.Select(record => record.Data).OfType<KernelEvent>().Select(candidate => candidate.OomKill).Single(candidate => candidate is { BootId.Length: > 0 });
        var process = records.Select(record => record.Data).OfType<ProcessSnapshot>().Single(candidate => candidate.Trigger is not null);
        var connection = records.Select(record => record.Data).OfType<ConnectionSnapshot>().Single(candidate => candidate.Trigger is not null);
        var runs = records.Where(record => record.Data is BackupRun).ToList();

        // Assert
        Assert.IsNotNull(kill, "the golden batch holds an OOM kill with an identity");
        Assert.AreEqual(4242, kill.VictimPid, "kill: victim pid");
        Assert.AreEqual(bootId, kill.BootId, "kill: boot_id");
        Assert.AreEqual(5_000_000_000L, kill.KernelTimeNs, "kill: kernel_time_ns");
        Assert.IsNull(kill.Validate(), "the kill is valid");
        Assert.ContainsSingle(records.Select(record => record.Data).OfType<KernelEvent>().Where(candidate => candidate.OomKill is { KernelTimeNs: null }).ToList(), "the plain OOM kill carries no kernel time");

        Assert.IsNotNull(process.Trigger!.Kills, "process snapshot: kills");
        Assert.HasCount(2, process.Trigger.Kills, "process snapshot: kills");
        Assert.AreEqual(bootId, process.Trigger.Kills[0].BootId, "process snapshot: first boot_id");
        Assert.AreEqual(5_000_000_000L, process.Trigger.Kills[0].KernelTimeNs, "process snapshot: first kernel_time_ns");
        Assert.AreEqual(4242, process.Trigger.Kills[0].VictimPid, "process snapshot: first victim_pid");
        Assert.AreEqual(previousBootId, process.Trigger.Kills[1].BootId, "process snapshot: second boot_id");
        Assert.AreEqual(7_250_000_000L, process.Trigger.Kills[1].KernelTimeNs, "process snapshot: second kernel_time_ns");
        Assert.AreEqual(77, process.Trigger.Kills[1].VictimPid, "process snapshot: second victim_pid");
        Assert.AreEqual(3u, process.Trigger.KillsOmitted, "process snapshot: kills_omitted");

        Assert.IsNotNull(connection.Trigger!.Kills, "connection snapshot: kills");
        Assert.HasCount(1, connection.Trigger.Kills, "connection snapshot: kills");
        Assert.AreEqual(4242, connection.Trigger.Kills[0].VictimPid, "connection snapshot: victim_pid");
        Assert.AreEqual(0u, connection.Trigger.KillsOmitted, "connection snapshot: kills_omitted");

        Assert.HasCount(2, runs, "backup runs of the golden batch");

        var start = (BackupRun)runs[0].Data!;
        var end = (BackupRun)runs[1].Data!;

        Assert.AreEqual("start", start.Phase, "start: phase");
        Assert.AreEqual(bootId, start.BootId, "start: boot_id");
        Assert.AreEqual(5150, start.Pid, "start: pid");
        Assert.AreEqual("pzstd", start.Command, "start: command");
        Assert.AreEqual(3u, start.Processes, "start: processes");
        Assert.AreEqual(start.StartedAt, runs[0].CapturedAt, "start: captured_at is started_at");
        Assert.AreEqual("backups", runs[0].Source, "start: source");
        Assert.IsNull(start.LastSeenAt, "start: last_seen_at");
        Assert.AreEqual("end", end.Phase, "end: phase");
        Assert.AreEqual(start.StartedAt, end.StartedAt, "end: started_at pairs it with the start");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 12, 30, 15, TimeSpan.Zero), end.LastSeenAt, "end: last_seen_at");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 12, 30, 15, TimeSpan.Zero).AddMilliseconds(500), end.GoneBy, "end: gone_by");
        Assert.AreEqual(4u, end.MaxProcesses, "end: max_processes");
        Assert.AreEqual(end.LastSeenAt, runs[1].CapturedAt, "end: captured_at is last_seen_at");
        Assert.IsNull(start.Validate(), "the start is valid");
        Assert.IsNull(end.Validate(), "the end is valid");
    }

    /// <summary>
    /// The log line of the Go encoder's golden batch carries every field, the host and the event included.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DecodeGoldenBatchReadsEveryLogLineField()
    {
        // Arrange
        var plain = await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/wire/all-kinds.jsonl"), CancellationToken.None);
        using var compressed = new MemoryStream();

        await using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
        {
            await gzip.WriteAsync(plain, CancellationToken.None);
        }

        compressed.Position = 0;

        // Act
        LogLine? line = null;

        using (var decoder = await BatchDecoder.OpenAsync(compressed, null, CancellationToken.None))
        {
            for (var record = await decoder.NextAsync(CancellationToken.None); record is not null; record = await decoder.NextAsync(CancellationToken.None))
            {
                if (record.Data is LogLine found)
                {
                    line = found;
                }
            }
        }

        // Assert
        Assert.IsNotNull(line, "the golden batch holds a log line");
        Assert.AreEqual("journal", line.Log, "log");
        Assert.AreEqual("web-1", line.Host, "host");
        Assert.AreEqual("sshd", line.Program, "program");
        Assert.AreEqual(5120, line.Pid, "pid");
        Assert.AreEqual((byte)3, line.Priority, "priority");
        Assert.AreEqual("Failed password", line.Message, "message");
        Assert.IsTrue(line.Truncated, "truncated");
        Assert.AreEqual("auth.failure", line.Event, "event");
    }

    /// <summary>
    /// The connection snapshots of the Go encoder's golden batch carry the fields of format 1.1, and the decoder applies their rules to the golden text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WireContractConnectionSnapshotCarriesTheNewFields()
    {
        // Arrange
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/wire/all-kinds.jsonl"), CancellationToken.None));
        const string completeFalse = "\"complete\":false,\"aggregates_incomplete\":true";

        Assert.Contains(completeFalse, text, "the golden batch holds a snapshot that is not complete and has incomplete aggregates");

        var snapshots = new List<ConnectionSnapshot>();

        using (var compressed = GzipOf(Encoding.UTF8.GetBytes(text)))
            using (var decoder = await BatchDecoder.OpenAsync(compressed, null, CancellationToken.None))
            {
                Assert.AreEqual(1L, decoder.Header.FormatMinor, "format minor of the golden batch");

                for (var record = await decoder.NextAsync(CancellationToken.None); record is not null; record = await decoder.NextAsync(CancellationToken.None))
                {
                    if (record.Data is ConnectionSnapshot snapshot)
                    {
                        snapshots.Add(snapshot);
                    }
                }
            }

        // Act
        var contradicting = text.Replace(completeFalse, "\"complete\":true,\"aggregates_incomplete\":true", StringComparison.Ordinal);

        using var contradictingInput = GzipOf(Encoding.UTF8.GetBytes(contradicting));
        using var contradictingDecoder = await BatchDecoder.OpenAsync(contradictingInput, null, CancellationToken.None);

        Assert.IsNotNull(await contradictingDecoder.NextAsync(CancellationToken.None), "first record of the golden batch");
        Assert.IsNotNull(await contradictingDecoder.NextAsync(CancellationToken.None), "second record of the golden batch");

        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => contradictingDecoder.NextAsync(CancellationToken.None), "a complete snapshot with incomplete aggregates");

        // Assert
        Assert.HasCount(4, snapshots, "connection snapshots of the golden batch");

        var full = snapshots[0];

        Assert.IsFalse(full.Complete, "full: complete");
        Assert.IsFalse(full.AggregatesOnly, "full: aggregates_only");
        Assert.IsTrue(full.AggregatesIncomplete, "full: aggregates_incomplete");
        Assert.AreEqual("all", full.ListenerCheck, "full: listener_check");
        Assert.IsNotNull(full.NewListeners, "full: new_listeners");
        Assert.HasCount(1, full.NewListeners, "full: new listeners");
        Assert.AreEqual("udp", full.NewListeners[0].Proto, "full: new listener proto");
        Assert.AreEqual("0.0.0.0:5353", full.NewListeners[0].Local?.ToString(), "full: new listener local");
        Assert.AreEqual(11, full.NewListeners[0].Pid, "full: new listener pid");
        Assert.AreEqual("avahi", full.NewListeners[0].Command, "full: new listener command");
        Assert.AreEqual(2u, full.NewListenersOmitted, "full: new_listeners_omitted");
        Assert.IsNotNull(full.Connections, "full: connections");
        Assert.IsNotNull(full.Remotes, "full: remotes");
        Assert.AreEqual("::ffff:192.0.2.7", full.Remotes[1].Addr?.ToString(), "full: mapped remote address");
        Assert.IsTrue(full.Remotes[1].Addr?.IsIPv4MappedToIPv6 ?? false, "full: the mapped address stays mapped");

        var aggregates = snapshots[1];

        Assert.IsFalse(aggregates.Complete, "aggregates: complete");
        Assert.IsTrue(aggregates.AggregatesOnly, "aggregates: aggregates_only");
        Assert.IsTrue(aggregates.AggregatesIncomplete, "aggregates: aggregates_incomplete");
        Assert.AreEqual("privileged", aggregates.ListenerCheck, "aggregates: listener_check");
        Assert.IsNull(aggregates.Connections, "aggregates: connections");
        Assert.AreEqual(0u, aggregates.NewListenersOmitted, "aggregates: new_listeners_omitted");
        Assert.IsNotNull(aggregates.NewListeners, "aggregates: new_listeners");
        Assert.HasCount(1, aggregates.NewListeners, "aggregates: new listeners");

        var start = snapshots[2];

        Assert.IsTrue(start.Complete, "start: complete");
        Assert.AreEqual("start", start.ListenerCheck, "start: listener_check");
        Assert.AreEqual("privileged", start.Inventory, "start: inventory");
        Assert.IsNotNull(start.Tables, "start: tables");
        Assert.AreSequenceEqual(["tcp", "udp6"], start.Tables, "start: tables in table order");
        Assert.IsNull(start.NewListeners, "start: new_listeners");
        Assert.AreEqual(0u, start.NewListenersOmitted, "start: new_listeners_omitted");
        Assert.IsNotNull(start.Listeners, "start: listeners");
        Assert.HasCount(1, start.Listeners, "start: listeners");
        Assert.IsNull(full.Inventory, "full: inventory is absent");
        Assert.IsNull(aggregates.Tables, "aggregates: tables is absent");

        Assert.AreEqual(WireErrorKind.Invalid, exception.Kind, "error class");
        Assert.AreEqual("data.complete", exception.FieldError?.Field, "field");
        Assert.AreEqual("must be false when aggregates are incomplete", exception.FieldError?.Reason, "reason");
    }

    /// <summary>
    /// The texts of the replaced-text golden batch sit exactly at their limits after the Go encoder's replacement of invalid bytes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DecodeReplacedTextBatchAcceptsTextsAtTheLimit()
    {
        // Arrange
        var plain = await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/wire/replaced-text.jsonl"), CancellationToken.None);
        using var compressed = GzipOf(plain);

        // Act
        LogLine? line = null;

        using (var decoder = await BatchDecoder.OpenAsync(compressed, null, CancellationToken.None))
        {
            for (var record = await decoder.NextAsync(CancellationToken.None); record is not null; record = await decoder.NextAsync(CancellationToken.None))
            {
                line = record.Data as LogLine ?? line;
            }
        }

        // Assert
        Assert.IsNotNull(line, "the golden batch holds a log line");
        Assert.AreEqual(16384, Encoding.UTF8.GetByteCount(line.Message), "message bytes");
        Assert.AreEqual(1024, Encoding.UTF8.GetByteCount(line.Host), "host bytes");
        Assert.Contains("�", line.Message, "message holds U+FFFD");
        Assert.Contains("�", line.Host, "host holds U+FFFD");
    }

    /// <summary>
    /// One more byte in the message of the replaced-text golden batch is refused as too long, and the whole batch with it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task DecodeReplacedTextBatchOneByteOverTheLimitIsRefused()
    {
        // Arrange
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(RepositoryFiles.Path("testdata/wire/replaced-text.jsonl"), CancellationToken.None));
        var marker = "\"message\":\"";

        Assert.Contains(marker, text, "the golden batch holds a message");

        var widened = text.Replace(marker, marker + "a", StringComparison.Ordinal);

        using var compressed = GzipOf(Encoding.UTF8.GetBytes(widened));

        // Act
        using var decoder = await BatchDecoder.OpenAsync(compressed, null, CancellationToken.None);
        var exception = await Assert.ThrowsExactlyAsync<WireException>(() => decoder.NextAsync(CancellationToken.None), "message one byte over the limit");

        // Assert
        Assert.AreEqual(WireErrorKind.Invalid, exception.Kind, "error class");
        Assert.AreEqual("data.message", exception.FieldError?.Field, "field");
        Assert.AreEqual("too long", exception.FieldError?.Reason, "reason");
    }

    /// <summary>
    /// Every shared decoder case (<c>testdata/wire/decoder-cases.json</c>), decoded to the end, gives the records and the error the case names.
    /// </summary>
    /// <param name="name">The name of the case</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DynamicData(nameof(SharedCaseNames), DynamicDataDisplayName = nameof(SharedCaseDisplayName))]
    public async Task DecodeSharedCaseGivesExpectedResult(string name)
    {
        // Arrange
        var matches = SharedCases().Where(candidate => candidate.GetProperty("name").GetString() == name).ToList();

        Assert.HasCount(1, matches, $"one case is named {name}");

        var lines = matches[0].GetProperty("lines").EnumerateArray().Select(line => line.GetString() ?? string.Empty).ToArray();
        var want = matches[0].GetProperty("want");

        if (_escapedCaseIds.Contains(name.Split(' ')[0]))
        {
            Assert.Contains(line => line.Contains('\\'), lines, $"{name}: a line holds a backslash, the escape of the key was kept");
        }

        var latin1 = matches[0].TryGetProperty("latin1", out var flag) && flag.GetBoolean();

        if (latin1)
        {
            Assert.DoesNotContain(line => line.Any(character => character > '\u00ff'), lines, $"{name}: a latin1 case holds no character above U+00FF");
            Assert.Contains(line => line.Any(character => character >= '\u0080'), lines, $"{name}: a latin1 case holds a character from U+0080 to U+00FF, the byte was kept");
        }

        // Act
        var (records, formatMinor, failure) = await ReadSharedCaseAsync(lines, latin1);

        // Assert
        Assert.AreSequenceEqual(want.TryGetProperty("records", out var wanted) ? wanted.EnumerateArray().Select(DescribeWanted).ToList() : [], records, $"{name}: records before the end or the error");

        if (want.TryGetProperty("error", out var error))
        {
            AssertSharedError(want, error.GetString() ?? string.Empty, failure);
        }
        else
        {
            Assert.IsNull(failure, $"{name}: no error, got {failure?.Message}");
        }

        if (want.TryGetProperty("format_minor", out var minor))
        {
            Assert.AreEqual(minor.GetInt64(), formatMinor, $"{name}: format_minor");
        }
    }

    /// <summary>
    /// The shared decoder cases have unique names and hold every case the plans of issues 175 and 182 list.
    /// </summary>
    [TestMethod]
    public void SharedCasesHaveUniqueNamesAndHoldEveryRequiredCase()
    {
        // Arrange
        var names = SharedCases().Select(candidate => candidate.GetProperty("name").GetString() ?? string.Empty).ToList();
        var groups = new (string Prefix, int Count)[]
                     {
                         ("K", 6),
                         ("N", 11),
                         ("D", 28),
                         ("H", 31),
                         ("O", 4),
                         ("X", 4),
                         ("M", 5),
                         ("U", 2),
                         ("S", 40)
                     };
        var required = groups.SelectMany(pair => Enumerable.Range(1, pair.Count).Select(number => $"{pair.Prefix}{number}"));

        // Act
        var missing = required.Except(names.Select(name => name.Split(' ')[0])).ToList();

        // Assert
        Assert.HasCount(names.Count, names.Distinct(StringComparer.Ordinal).ToList(), "case names are unique");
        Assert.IsEmpty(missing, $"cases missing from the fixture: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Reads the shared decoder cases.
    /// </summary>
    /// <returns>The cases</returns>
    private static List<JsonElement> SharedCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(RepositoryFiles.Path("testdata/wire/decoder-cases.json")));

        return document.RootElement.GetProperty("cases").EnumerateArray().Select(element => element.Clone()).ToList();
    }

    /// <summary>
    /// Describes a record that a shared case expects.
    /// </summary>
    /// <param name="record">The expected record with its kind and sequence number</param>
    /// <returns>The description</returns>
    private static string DescribeWanted(JsonElement record)
    {
        return $"{record.GetProperty("kind").GetString()}:{record.GetProperty("seq").GetUInt64()}";
    }

    /// <summary>
    /// Gzips the lines of a case and decodes the batch to its end. A <see cref="WireException"/> is part of the outcome, any other exception is not.
    /// </summary>
    /// <param name="lines">The lines of the batch</param>
    /// <param name="latin1"><c>true</c> to write every character of the lines as one byte, <c>false</c> to encode the lines as UTF-8</param>
    /// <returns>The descriptions of the records, the minor version of the header and the error, if any</returns>
    private static async Task<(List<string> Records, long FormatMinor, WireException? Failure)> ReadSharedCaseAsync(string[] lines, bool latin1)
    {
        var records = new List<string>();
        long formatMinor = 0;
        using var input = latin1 ? BatchBuilder.Gzip(Encoding.Latin1.GetBytes(string.Concat(lines.Select(line => line + "\n")))) : BatchBuilder.Gzip(lines);

        try
        {
            using var decoder = await BatchDecoder.OpenAsync(input, null, CancellationToken.None);

            formatMinor = decoder.Header.FormatMinor;

            for (var record = await decoder.NextAsync(CancellationToken.None); record is not null; record = await decoder.NextAsync(CancellationToken.None))
            {
                records.Add($"{record.Kind}:{record.Seq}");
            }
        }
        catch (WireException exception)
        {
            return (records, formatMinor, exception);
        }

        return (records, formatMinor, null);
    }

    /// <summary>
    /// Compresses a plaintext batch.
    /// </summary>
    /// <param name="plain">The plaintext batch</param>
    /// <returns>The compressed batch, positioned at its start</returns>
    private static MemoryStream GzipOf(byte[] plain)
    {
        var compressed = new MemoryStream();

        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
        {
            gzip.Write(plain);
        }

        compressed.Position = 0;

        return compressed;
    }

    /// <summary>
    /// Checks the error of a shared case.
    /// </summary>
    /// <param name="want">The expected outcome of the case</param>
    /// <param name="name">The name of the expected class of the error</param>
    /// <param name="failure">The error that was thrown, if any</param>
    private static void AssertSharedError(JsonElement want, string name, WireException? failure)
    {
        Assert.Contains(name, Enum.GetNames<WireErrorKind>(), $"{name} is one of the seven error names");
        Assert.IsNotNull(failure, $"error {name} expected");
        Assert.AreEqual(Enum.Parse<WireErrorKind>(name), failure.Kind, $"error class, got: {failure.Message}");
        Assert.AreEqual(want.GetProperty("line").GetInt32(), failure.Line, "line of the error");

        if (want.TryGetProperty("field", out var field))
        {
            Assert.AreEqual(field.GetString(), failure.FieldError?.Field, "field of the error");
        }

        if (want.TryGetProperty("reason", out var reason))
        {
            Assert.AreEqual(reason.GetString(), failure.FieldError?.Reason, "reason of the error");
        }
    }

    #endregion // Methods
}