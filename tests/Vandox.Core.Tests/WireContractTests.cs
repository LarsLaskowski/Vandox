using System.IO.Compression;

using Vandox.Core.Model;
using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Contract tests that decode the plaintext batch the Go agent's encoder writes (<c>testdata/wire/all-kinds.jsonl</c>).
/// </summary>
[TestClass]
public class WireContractTests
{
    #region Methods

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
    /// The log line of the Go encoder's golden batch carries every field, the host included.
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
    }

    #endregion // Methods
}