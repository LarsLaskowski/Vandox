using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

using Vandox.Backend.Hosting;
using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ConnectionCap"/>
/// </summary>
[TestClass]
public class ConnectionCapTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Connections beyond a cap of trusted connections are aborted without reaching the next delegate, and a slot that a finished connection frees is usable
    /// again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapAbortsTheConnectionBeyondACapOfTrustedConnectionsAndReusesFreedSlots()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var cap = new ConnectionCap(2);
        var wrapped = cap.Wrap(_ =>
                               {
                                   Interlocked.Increment(ref entered);

                                   return release.Task;
                               });
        var first = new AbortRecordingConnection();
        var second = new AbortRecordingConnection();
        var third = new AbortRecordingConnection();
        var fourth = new AbortRecordingConnection();

        // Act
        var firstRun = wrapped(first);
        var secondRun = wrapped(second);

        Trust(first);
        Trust(second);

        await wrapped(third).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        var enteredWhileFull = Volatile.Read(ref entered);

        release.SetResult();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        await wrapped(fourth).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, enteredWhileFull, "only two connections reach the next delegate");
        Assert.AreEqual(0, first.Aborts, "the first connection is served");
        Assert.AreEqual(0, second.Aborts, "the second connection is served");
        Assert.AreEqual(1, third.Aborts, "the third connection is aborted");
        Assert.AreEqual(0, fourth.Aborts, "a freed slot serves the next connection");
        Assert.AreEqual(3, Volatile.Read(ref entered), "the fourth connection reaches the next delegate");
    }

    /// <summary>
    /// A new connection over the cap evicts the oldest untrusted connection: that one is aborted once, the new one is served, nobody else is aborted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapEvictsTheOldestUntrustedConnectionWhenFull()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var cap = new ConnectionCap(2);
        var wrapped = cap.Wrap(_ =>
                               {
                                   Interlocked.Increment(ref entered);

                                   return release.Task;
                               });
        var first = new AbortRecordingConnection();
        var second = new AbortRecordingConnection();
        var third = new AbortRecordingConnection();

        // Act
        var firstRun = wrapped(first);
        var secondRun = wrapped(second);
        var thirdRun = wrapped(third);
        var enteredWhileFull = Volatile.Read(ref entered);

        release.SetResult();
        await Task.WhenAll(firstRun, secondRun, thirdRun).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3, enteredWhileFull, "the new connection reaches the next delegate");
        Assert.AreEqual(1, first.Aborts, "the oldest untrusted connection is aborted once");
        Assert.AreEqual(0, second.Aborts, "the second connection is not aborted");
        Assert.AreEqual(0, third.Aborts, "the new connection is not aborted");
    }

    /// <summary>
    /// A trusted connection is never the one that is evicted: the oldest untrusted connection is.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapSkipsTrustedConnectionsWhenEvicting()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var cap = new ConnectionCap(2);
        var wrapped = cap.Wrap(_ =>
                               {
                                   Interlocked.Increment(ref entered);

                                   return release.Task;
                               });
        var first = new AbortRecordingConnection();
        var second = new AbortRecordingConnection();
        var third = new AbortRecordingConnection();

        // Act
        var firstRun = wrapped(first);
        var secondRun = wrapped(second);

        Trust(first);

        var thirdRun = wrapped(third);
        var enteredWhileFull = Volatile.Read(ref entered);

        release.SetResult();
        await Task.WhenAll(firstRun, secondRun, thirdRun).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3, enteredWhileFull, "the new connection reaches the next delegate");
        Assert.AreEqual(0, first.Aborts, "the trusted connection is not aborted");
        Assert.AreEqual(1, second.Aborts, "the untrusted connection is aborted once");
        Assert.AreEqual(0, third.Aborts, "the new connection is not aborted");
    }

    /// <summary>
    /// An evicted connection stops counting at its eviction, and its later end frees nothing more.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapCountsAnEvictedConnectionOnce()
    {
        // Arrange
        const string gateName = "test.gate";

        var entered = 0;
        var cap = new ConnectionCap(1);
        var wrapped = cap.Wrap(context =>
                               {
                                   Interlocked.Increment(ref entered);

                                   return ((TaskCompletionSource)context.Items[gateName]!).Task;
                               });
        var first = new AbortRecordingConnection();
        var second = new AbortRecordingConnection();
        var third = new AbortRecordingConnection();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        first.Items[gateName] = firstGate;
        second.Items[gateName] = secondGate;

        // Act
        var firstRun = wrapped(first);
        var secondRun = wrapped(second);

        firstGate.SetResult();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        Trust(second);

        var thirdRun = wrapped(third);
        var enteredAfterThird = Volatile.Read(ref entered);

        secondGate.SetResult();
        await Task.WhenAll(secondRun, thirdRun).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, first.Aborts, "the first connection is evicted once");
        Assert.AreEqual(0, second.Aborts, "the trusted second connection is not aborted");
        Assert.AreEqual(1, third.Aborts, "the end of the evicted connection freed no place for the third");
        Assert.AreEqual(2, enteredAfterThird, "the third connection does not reach the next delegate");
    }

    /// <summary>
    /// The cap puts its own connection item beside the listener label, and the two keys differ.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapKeepsTheListenerItemBesideItsOwn()
    {
        // Arrange
        var cap = new ConnectionCap(2);
        object? capped = null;
        object? label = null;
        var wrapped = cap.Wrap(context =>
                               {
                                   context.Items.TryGetValue(ConnectionCap.ItemName, out capped);
                                   context.Items.TryGetValue(ListenerRoutes.ItemName, out label);

                                   return Task.CompletedTask;
                               });
        var connection = new AbortRecordingConnection();

        connection.Items[ListenerRoutes.ItemName] = "ingest";

        // Act
        await wrapped(connection).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        var held = capped as CappedConnection;

        Assert.AreEqual("ingest", label, "the listener label is kept");
        Assert.IsNotNull(held, "the held connection is present beside the label");
        Assert.AreSame(connection, held.Connection, "the held connection is the one the cap was handed");
    }

    /// <summary>
    /// A request without the connection item (the web listener, a test context) is left as it is.
    /// </summary>
    [TestMethod]
    public void ConnectionCapTrustIgnoresAContextWithoutTheConnectionItem()
    {
        // Arrange
        var withoutFeature = new DefaultHttpContext();
        var withoutItem = new DefaultHttpContext();

        var items = new Dictionary<object, object?>();

        withoutItem.Features.Set<IConnectionItemsFeature>(new ConnectionItems(items));

        // Act
        ConnectionCap.Trust(withoutFeature);
        ConnectionCap.Trust(withoutItem);

        // Assert
        Assert.IsEmpty(items, "nothing is added to the items");
    }

    /// <summary>
    /// A connection whose handler throws still frees its slot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapFreesTheSlotWhenTheHandlerThrows()
    {
        // Arrange
        var cap = new ConnectionCap(1);
        var wrapped = cap.Wrap(_ => throw new InvalidOperationException("handler failed"));
        var next = cap.Wrap(_ => Task.CompletedTask);
        var failing = new AbortRecordingConnection();
        var later = new AbortRecordingConnection();

        // Act
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => wrapped(failing), "the failure of the handler passes through");
        await next(later).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, failing.Aborts, "the failing connection is not aborted by the cap");
        Assert.AreEqual(0, later.Aborts, "the slot is free again");
    }

    /// <summary>
    /// Trusts the connection the way the endpoint does: through the connection items of a request context.
    /// </summary>
    /// <param name="connection">The connection the cap was handed, after it reached the next delegate</param>
    private static void Trust(AbortRecordingConnection connection)
    {
        var context = new DefaultHttpContext();

        context.Features.Set<IConnectionItemsFeature>(new ConnectionItems(connection.Items));
        ConnectionCap.Trust(context);
    }

    #endregion // Methods
}