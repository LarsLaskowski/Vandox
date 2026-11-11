using System.Net;
using System.Net.Sockets;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="BackendFixture"/>
/// </summary>
[TestClass]
public class BackendFixtureTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reading the log entries while another thread writes JSON lines to standard error never throws and ends with every line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task LogEntriesWhileLoggingParsesEveryLine()
    {
        // Arrange
        const int lineCount = 5000;
        using var fixture = new BackendFixture();
        var counts = new List<int>();

        // Act
        var producer = Task.Run(() =>
                                {
                                    for (var index = 0; index < lineCount; index++)
                                    {
                                        fixture.Error.WriteLine($"{{\"msg\":\"entry\",\"n\":{index}}}");
                                    }
                                },
                                TestContext.CancellationToken);

        while (producer.Status < TaskStatus.RanToCompletion)
        {
            counts.Add(fixture.LogEntries().Count);
        }

        await producer;

        var final = fixture.LogEntries();

        // Assert
        Assert.IsInstanceOfType<LockedTextWriter>(fixture.Error, "the captured standard error is a locked writer");
        Assert.IsTrue(counts.All(count => count <= lineCount), "no call returns more entries than lines written");
        Assert.IsTrue(counts.Zip(counts.Skip(1)).All(pair => pair.First <= pair.Second), "every call returns at least the entries of the call before");
        Assert.HasCount(lineCount, final, "the last call returns every line");
        Assert.AreEqual((lineCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), final[^1]["n"], "the last entry is the last line written");
    }

    /// <summary>
    /// A start whose web port was taken after it was chosen runs again on new ports and returns the run that started.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task BackendFixtureStartServiceAsyncStartsAgainOnNewPortsWhenThePortIsTaken()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var firstWebPort = fixture.WebPort;
        var firstIngestPort = fixture.IngestPort;
        using var holder = new TcpListener(IPAddress.Loopback, firstWebPort);

        holder.Start();
        fixture.UseTrustedProxies("127.0.0.1");

        // Act
        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);
        var configuration = await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.CancellationToken);

        await stop.CancelAsync();

        var exitCode = await run;

        // Assert
        Assert.AreNotEqual(firstWebPort, fixture.WebPort, "the web port is a newly chosen one");
        Assert.AreNotEqual(firstIngestPort, fixture.IngestPort, "the ingest port is a newly chosen one");
        Assert.AreEqual(0, exitCode, "the returned run is the one that started and stopped cleanly");
        Assert.Contains($"listen: 127.0.0.1:{fixture.WebPort}\n", configuration, "the configuration file holds the new web port");
        Assert.Contains($"listen: 127.0.0.1:{fixture.IngestPort}\n", configuration, "the configuration file holds the new ingest port");
        Assert.Contains("trusted_proxies: \"127.0.0.1\"", configuration, "the configuration file keeps the trusted-proxies option");
        Assert.DoesNotContain("ServingFailed", fixture.Error.ToString(), "the output of the failed attempt was cleared");
    }

    /// <summary>
    /// A start that ends before it reports its start in every attempt throws with the exit code and the standard error of every attempt.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task BackendFixtureStartServiceAsyncThrowsWithTheOutputWhenEveryAttemptEnds()
    {
        // Arrange
        using var fixture = new BackendFixture();

        fixture.UseTrustedProxies("not-an-address");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.StartServiceAsync(TestContext.CancellationToken, TestContext.CancellationToken));

        // Assert
        Assert.Contains("attempt 1", exception.Message, "the message names the first attempt");
        Assert.Contains("attempt 2", exception.Message, "the message names the second attempt");
        Assert.Contains("attempt 3", exception.Message, "the message names the third attempt");
        Assert.DoesNotContain("attempt 4", exception.Message, "there are three attempts in total");
        Assert.Contains("web.trusted_proxies", exception.Message, "the message holds the configuration error");
        Assert.Contains("exit code 1", exception.Message, "the message holds the exit code");
    }

    #endregion // Methods
}