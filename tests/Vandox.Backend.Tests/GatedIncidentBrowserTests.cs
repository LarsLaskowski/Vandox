using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedIncidentBrowser"/>
/// </summary>
[TestClass]
public class GatedIncidentBrowserTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads go through the gate, so with one slot the inner browser sees one read at a time, whichever read it is, while a note save does not wait for the gate.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedIncidentBrowserSharesTheGateForReadsButNotForNoteSaves()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FakeIncidentBrowser
                    {
                        Before = (method, token) => method == FakeIncidentBrowser.SaveIncidentNote ? Task.CompletedTask : release.Task.WaitAsync(token)
                    };

        using var gate = new WebReaderGate(1);

        var browser = new GatedIncidentBrowser(inner, gate);
        var edit = new IncidentNoteEdit
                   {
                       IncidentId = 4,
                       WrittenFor = 4,
                       Text = "a note",
                       SavedAt = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)
                   };

        // Act
        var list = browser.IncidentSummariesAsync(new IncidentListQuery
                                                  {
                                                      Limit = 20
                                                  },
                                                  TestContext.CancellationToken);

        await inner.WaitForCallsAsync(FakeIncidentBrowser.IncidentSummaries, 1);

        var one = browser.IncidentAsync(4, TestContext.CancellationToken);
        var notes = browser.IncidentNotesAsync(4, TestContext.CancellationToken);
        var save = await browser.SaveIncidentNoteAsync(edit, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        var readsWhileBlocked = inner.Calls.Count(call => call.Method != FakeIncidentBrowser.SaveIncidentNote);

        release.SetResult();

        await Task.WhenAll(list, one, notes).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(IncidentNoteOutcome.Saved, save, "the save was answered while a read held the only slot");
        Assert.AreEqual(1, readsWhileBlocked, "the inner browser sees one read while the slot is taken");
        Assert.HasCount(1, inner.Queries<IncidentNoteEdit>(FakeIncidentBrowser.SaveIncidentNote), "the save reached the inner browser");
        Assert.AreEqual("a note", inner.Queries<IncidentNoteEdit>(FakeIncidentBrowser.SaveIncidentNote)[0].Text, "the edit reaches the inner browser");
        Assert.HasCount(1, inner.Queries<long>(FakeIncidentBrowser.Incident), "the read of one incident ran after the slot was free");
        Assert.HasCount(1, inner.Queries<long>(FakeIncidentBrowser.IncidentNotes), "the read of the notes ran after the slot was free");
    }

    /// <summary>
    /// A read that waits for the slot ends with its token and never reaches the inner browser.
    /// </summary>
    /// <param name="method">The read to call</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(FakeIncidentBrowser.IncidentSummaries)]
    [DataRow(FakeIncidentBrowser.Incident)]
    [DataRow(FakeIncidentBrowser.IncidentNotes)]
    public async Task GatedIncidentBrowserReadWaitEndsWithTheToken(string method)
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new FakeIncidentBrowser
                    {
                        Before = (_, token) => release.Task.WaitAsync(token)
                    };

        using var gate = new WebReaderGate(1);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var browser = new GatedIncidentBrowser(inner, gate);
        var first = ReadAsync(browser, method, TestContext.CancellationToken);

        await inner.WaitForCallsAsync(method, 1);

        var second = ReadAsync(browser, method, waiting.Token);

        // Act
        await waiting.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => second);

        release.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(exception, "the waiting read ended with an OperationCanceledException");
        Assert.HasCount(1, inner.Calls, "the inner browser was called once, by the first read");
    }

    /// <summary>
    /// Calls one read of a browser with default arguments.
    /// </summary>
    /// <param name="browser">The browser</param>
    /// <param name="method">The name of the method</param>
    /// <param name="cancellationToken">The token</param>
    /// <returns>A task that completes when the call is done</returns>
    private static Task ReadAsync(GatedIncidentBrowser browser, string method, CancellationToken cancellationToken)
    {
        return method switch
               {
                   FakeIncidentBrowser.IncidentSummaries => browser.IncidentSummariesAsync(new IncidentListQuery
                                                                                           {
                                                                                               Limit = 20
                                                                                           },
                                                                                           cancellationToken),
                   FakeIncidentBrowser.Incident => browser.IncidentAsync(1, cancellationToken),
                   FakeIncidentBrowser.IncidentNotes => browser.IncidentNotesAsync(1, cancellationToken),
                   _ => throw new ArgumentOutOfRangeException(nameof(method), method, "unknown method")
               };
    }

    #endregion // Methods
}