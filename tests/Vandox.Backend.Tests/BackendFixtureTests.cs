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
        Assert.HasCount(lineCount, final, "the last call returns every line");
        Assert.AreEqual((lineCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), final[^1]["n"], "the last entry is the last line written");
    }

    #endregion // Methods
}