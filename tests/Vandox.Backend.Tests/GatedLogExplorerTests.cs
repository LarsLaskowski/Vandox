using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedLogExplorer"/>
/// </summary>
[TestClass]
public class GatedLogExplorerTests
{
    #region Constants

    private const string FindLogs = FakeLogExplorer.FindLogs;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// With two slots and three blocked calls the inner explorer sees two, and the third starts after one ends.
    /// </summary>
    /// <param name="method">The method to call</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(FakeLogExplorer.FindLogs)]
    [DataRow(FakeLogExplorer.LogContext)]
    [DataRow(FakeLogExplorer.LogSources)]
    [DataRow(FakeLogExplorer.Template)]
    [DataRow(FakeLogExplorer.Templates)]
    [DataRow(FakeLogExplorer.SourceHours)]
    public async Task GatedLogExplorerRunsAtMostTheGivenNumberOfCallsAtOnce(string method)
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FakeLogExplorer
                    {
                        Before = (_, token) => release.Task.WaitAsync(token)
                    };

        using var gate = new GatedLogExplorer(inner, 2);

        // Act
        var calls = Enumerable.Range(0, 3).Select(_ => CallAsync(gate, method, TestContext.CancellationToken)).ToArray();

        await inner.WaitForCallsAsync(method, 2);

        var whileBlocked = inner.Calls.Count;

        release.SetResult();

        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, whileBlocked, "the inner explorer sees two of the three calls while they are blocked");
        Assert.HasCount(3, inner.Calls, "the third call ran after a slot was free");
        Assert.AreEqual(2, inner.MaxInFlight, "never more than two calls in flight");
    }

    /// <summary>
    /// A call that waits for a slot ends with its token and never reaches the inner explorer.
    /// </summary>
    /// <param name="method">The method to call</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(FakeLogExplorer.FindLogs)]
    [DataRow(FakeLogExplorer.LogContext)]
    [DataRow(FakeLogExplorer.LogSources)]
    [DataRow(FakeLogExplorer.Template)]
    [DataRow(FakeLogExplorer.Templates)]
    [DataRow(FakeLogExplorer.SourceHours)]
    public async Task GatedLogExplorerWaitEndsWithTheToken(string method)
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FakeLogExplorer
                    {
                        Before = (_, token) => release.Task.WaitAsync(token)
                    };

        using var gate = new GatedLogExplorer(inner, 1);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var first = CallAsync(gate, method, TestContext.CancellationToken);

        await inner.WaitForCallsAsync(method, 1);

        var second = CallAsync(gate, method, waiting.Token);

        // Act
        await waiting.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => second);

        release.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(exception, "the waiting call ended with an OperationCanceledException");
        Assert.HasCount(1, inner.Calls, "the inner explorer was called once, by the first call");
    }

    /// <summary>
    /// The slot is free again after a call failed and after a call was cancelled.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedLogExplorerReleasesTheSlotAfterAFailure()
    {
        // Arrange
        var inner = new FakeLogExplorer();

        inner.Failures[FindLogs] = new InvalidOperationException("scripted");

        using var gate = new GatedLogExplorer(inner, 1);

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(gate, FindLogs, TestContext.CancellationToken));

        inner.Failures.Clear();

        await CallAsync(gate, FindLogs, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        inner.Before = (_, token) => Task.Delay(Timeout.Infinite, token);

        var cancelled = CallAsync(gate, FindLogs, stop.Token);

        await inner.WaitForCallsAsync(FindLogs, 3);
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);

        inner.Before = null;

        await CallAsync(gate, FindLogs, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("scripted", failure.Message, "the failure of the inner call reaches the caller");
        Assert.HasCount(4, inner.Calls, "the calls after a failure and after a cancellation reached the inner explorer");
    }

    /// <summary>
    /// A slot count outside 1 to 3 is refused.
    /// </summary>
    /// <param name="slots">The slot count</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(4)]
    public void GatedLogExplorerRefusesAnInvalidSlotCount(int slots)
    {
        // Arrange
        var inner = new FakeLogExplorer();

        // Act
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GatedLogExplorer(inner, slots));

        // Assert
        Assert.IsNotNull(exception, $"{slots} slots are refused");
    }

    /// <summary>
    /// Calls one method of an explorer with default arguments.
    /// </summary>
    /// <param name="explorer">The explorer</param>
    /// <param name="method">The name of the method</param>
    /// <param name="cancellationToken">The token</param>
    /// <returns>A task that completes when the call is done</returns>
    private static Task CallAsync(GatedLogExplorer explorer, string method, CancellationToken cancellationToken)
    {
        return method switch
               {
                   FakeLogExplorer.FindLogs => explorer.FindLogsAsync(new LogFilter(), cancellationToken),
                   FakeLogExplorer.LogContext => explorer.LogContextAsync(new LogContextQuery(), cancellationToken),
                   FakeLogExplorer.LogSources => explorer.LogSourcesAsync(cancellationToken),
                   FakeLogExplorer.Template => explorer.TemplateAsync(1, cancellationToken),
                   FakeLogExplorer.Templates => explorer.TemplatesAsync(new TemplateListQuery(), cancellationToken),
                   FakeLogExplorer.SourceHours => explorer.SourceHoursAsync(new SourceHourQuery(), cancellationToken),
                   _ => throw new ArgumentOutOfRangeException(nameof(method), method, "unknown method")
               };
    }

    #endregion // Methods
}