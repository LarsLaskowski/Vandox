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
        string agentId;

        using (var decoder = await BatchDecoder.OpenAsync(compressed, null, CancellationToken.None))
        {
            agentId = decoder.Header.AgentId;

            for (var record = await decoder.NextAsync(CancellationToken.None); record is not null; record = await decoder.NextAsync(CancellationToken.None))
            {
                kinds.Add(record.Kind);
            }
        }

        // Assert
        Assert.AreEqual("agent-1", agentId, "agent id");

        var expected = new[] { RecordKind.Metric, RecordKind.ProcessSnapshot, RecordKind.ConnectionSnapshot, RecordKind.ServiceState, RecordKind.MariaDbStatus, RecordKind.KernelEvent, RecordKind.KernelEvent, RecordKind.LogLine, RecordKind.Gap };

        Assert.AreSequenceEqual(expected, kinds, "kinds in order");
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
                         ("S", 32)
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