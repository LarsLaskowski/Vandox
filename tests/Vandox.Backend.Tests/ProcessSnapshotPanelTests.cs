using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProcessSnapshotPanel"/>
/// </summary>
[TestClass]
public class ProcessSnapshotPanelTests
{
    #region Methods

    /// <summary>
    /// Without a snapshot the panel says that none was stored near the moment.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessSnapshotPanelWithoutSnapshotSaysSo()
    {
        // Arrange
        await using var services = Services();

        using var renderer = new ComponentTestRenderer(services, services.GetRequiredService<ILoggerFactory>());

        // Act
        var id = await renderer.AddAsync<ProcessSnapshotPanel>(Parameters(null, null, []));

        // Assert
        Assert.Contains(HistoryView.NoProcessSnapshotText, renderer.ToHtml(id), "the text for no snapshot");
        Assert.DoesNotContain("<h3>", renderer.ToHtml(id), "no heading");
    }

    /// <summary>
    /// A snapshot is shown with its note and the count of listed processes, and the button for the snapshot before reports its time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ProcessSnapshotPanelShowsNotesAndReportsTheSnapshotBefore()
    {
        // Arrange
        await using var services = Services();

        using var renderer = new ComponentTestRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var before = HistorySamples.Base.AddMinutes(-3);
        var chosen = new List<DateTimeOffset>();
        var view = new ProcessSnapshotView
                   {
                       CapturedAt = HistorySamples.Base,
                       Note = "The list is incomplete.",
                       ListedOfTotal = "2 of 9 processes are listed."
                   };

        var id = await renderer.AddAsync<ProcessSnapshotPanel>(Parameters(view, before, chosen));

        // Act
        var button = renderer.Elements(id).Single(element => element.Tag == "button");

        await renderer.ClickAsync(button);

        // Assert
        var html = renderer.ToHtml(id);

        Assert.Contains("The list is incomplete.", html, "the note of the snapshot");
        Assert.Contains("2 of 9 processes are listed.", html, "the count of the listed processes");
        Assert.Contains("Snapshot before this moment", html, "the button for the snapshot before");
        Assert.AreSequenceEqual([before], chosen.ToArray(), "the click reports the time of the snapshot before");
    }

    /// <summary>
    /// Creates the services of the component.
    /// </summary>
    /// <returns>The services</returns>
    private static ServiceProvider Services()
    {
        return new ServiceCollection().AddLogging().BuildServiceProvider();
    }

    /// <summary>
    /// Creates the parameters of the panel.
    /// </summary>
    /// <param name="process">The snapshot</param>
    /// <param name="before">The time of the snapshot before</param>
    /// <param name="chosen">Receives the times the button reports</param>
    /// <returns>The parameters</returns>
    private static Dictionary<string, object?> Parameters(ProcessSnapshotView? process, DateTimeOffset? before, List<DateTimeOffset> chosen)
    {
        return new Dictionary<string, object?>
               {
                   [nameof(ProcessSnapshotPanel.Process)] = process,
                   [nameof(ProcessSnapshotPanel.Before)] = before,
                   [nameof(ProcessSnapshotPanel.BeforeChosen)] = EventCallback.Factory.Create<DateTimeOffset>(chosen, (Action<DateTimeOffset>)chosen.Add)
               };
    }

    #endregion // Methods
}