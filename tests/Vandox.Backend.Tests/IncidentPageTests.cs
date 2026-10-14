using System.Globalization;
using System.Text;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Core.Incidents;
using Vandox.Storage;

using static Vandox.Backend.Tests.LogViewSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentPage"/>. The tests read the page the way a browser shows it: links by their address, forms by their fields, charts by the calls to the script.
/// </summary>
[TestClass]
public class IncidentPageTests
{
    #region Constants

    private const string Address = "http://localhost/incidents/7";

    private const ulong Mib = 1048576UL;

    #endregion // Constants

    #region Fields

    private static readonly string[] _timelineOrder = ["Linux 5.15", "backup_first_seen", "service_start", "backup_last_seen", "oom_kill"];

    private static readonly string[] _markClasses = ["mark-backup", "mark-boot", "mark-oom"];

    private static readonly string[] _unmarkedRows = ["row-service", "row-quote", "row-markup"];

    private static readonly string[] _writtenForNumbers = ["3", "4", "7"];

    private static readonly CompositeFormat _writtenFor = CompositeFormat.Parse(IncidentsView.WrittenFor);

    private static readonly CompositeFormat _chartCut = CompositeFormat.Parse(IncidentsView.ChartCut);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The page shows the header, the kept kills, the hypothesis, the timeline in stored order with the entries that were not kept, the connection peak, the metric extremes and the templates, and says that no data was stored for the window.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentPageShowsHeaderKillsHypothesisAndTimeline()
    {
        // Arrange
        await using var harness = Open(IncidentViewSamples.Full());
        await using var unknown = Open(Unknown(IncidentViewSamples.Full()));

        // Act
        await harness.AddAsync<IncidentPage>(IncidentViewSamples.Id);
        await unknown.AddAsync<IncidentPage>(IncidentViewSamples.Id);
        await harness.WaitForHtmlAsync(html => html.Contains(Enc(IncidentsView.NoWindowData), StringComparison.Ordinal));

        var html = harness.Html();
        var order = _timelineOrder.Select(text => html.IndexOf(text, StringComparison.Ordinal)).ToArray();

        // Assert
        Assert.Contains("Incident #7", html, "the title of the page");
        Assert.AreEqual(IncidentViewSamples.Id, harness.Browser.Queries<long>(FakeIncidentBrowser.Incident).Single(), "the incident is read by its number");
        Assert.AreEqual(IncidentViewSamples.Id, harness.Browser.Queries<long>(FakeIncidentBrowser.IncidentNotes).Single(), "its notes are read by its number");
        Assert.Contains("web-1", html, "the host");
        Assert.Contains("mariadbd", html, "the first victim");
        Assert.Contains("php-fpm", html, "the second victim");
        Assert.Contains("4242", html, "the process ID of the first victim");
        Assert.Contains(IncidentsView.Mib(7400UL * Mib), html, "the anonymous resident memory of the first victim");
        Assert.Contains("200", html, "the oom score of the second victim");
        Assert.Contains(LogsView.ShowSeconds(IncidentViewSamples.FirstKill), html, "the time of the first kill");
        AssertLabelled(html, "Duration", IncidentsView.Duration(IncidentViewSamples.LastKill - IncidentViewSamples.FirstKill), "the duration from the first to the last kill");
        AssertLabelled(html, "Built (UTC)", LogsView.ShowSeconds(IncidentViewSamples.Start.AddDays(1)), "when the incident was built");
        AssertLabelled(html, "Last changed (UTC)", LogsView.ShowSeconds(IncidentViewSamples.Start.AddDays(1).AddMinutes(5)), "when the incident was last changed");
        AssertLabelled(html, "Snapshots", "9", "the snapshots of the connection peak");
        Assert.Contains("37", html, "the number of timeline entries that were not kept");
        Assert.IsTrue(order.All(index => index >= 0), "every timeline entry is shown");
        Assert.AreSequenceEqual(order.Order().ToArray(), order, "the timeline is in stored order");
        Assert.Contains(Enc(IncidentsView.Growth(768UL * Mib)), html, "the growth of the program that grew most");
        Assert.Contains("apache2", html, "the largest consumer");
        Assert.Contains(IncidentsView.Mib(400UL * Mib), html, "its resident memory");
        Assert.Contains(IncidentsView.BackupRunning, html, "the backup state");
        Assert.Contains("321", html, "the sockets of the connection peak");
        Assert.Contains("sshd", html, "a process of the connection peak");
        Assert.Contains("mem_available_bytes", html, "a metric extreme");
        Assert.Contains("swap_used_bytes", html, "another metric extreme");
        Assert.Contains("proc.meminfo", html, "the source of a metric extreme");
        Assert.Contains(Enc("job <num> failed"), html, "a template");
        Assert.DoesNotContain("<num>", html, "a template is text");
        Assert.Contains(Enc(IncidentsView.NoWindowData), html, "no snapshot or metric was stored in the window");
        Assert.Contains(IncidentsView.Unknown, unknown.Html(), "a hypothesis that is unknown says so");
    }

    /// <summary>
    /// The row of an oom kill, a boot and a backup first or last seen carries its mark class, and any other type, also one with quotes or markup, none and appears as escaped text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentPageMarksOomKillsBootsAndBackupActivity()
    {
        // Arrange
        var quoted = "x\" onclick=\"evil";
        var markup = "<b>bold</b>";
        var stored = IncidentViewSamples.Full();

        stored.Incident.Timeline = [
                                       IncidentViewSamples.Entry(IncidentViewSamples.Start.AddMinutes(1), "oom_kill", "id", "row-oom"),
                                       IncidentViewSamples.Entry(IncidentViewSamples.Start.AddMinutes(2), "boot", "id", "row-boot"),
                                       IncidentViewSamples.Entry(IncidentViewSamples.Start.AddMinutes(3), "backup_first_seen", "id", "row-first"),
                                       IncidentViewSamples.Entry(IncidentViewSamples.Start.AddMinutes(4), "backup_last_seen", "id", "row-last"),
                                       IncidentViewSamples.Entry(IncidentViewSamples.Start.AddMinutes(5), "service_start", "id", "row-service"),
                                       IncidentViewSamples.Entry(IncidentViewSamples.Start.AddMinutes(6), quoted, "id", "row-quote"),
                                       IncidentViewSamples.Entry(IncidentViewSamples.Start.AddMinutes(7), markup, "id", "row-markup")
                                   ];

        await using var harness = Open(stored);

        // Act
        await harness.AddAsync<IncidentPage>(IncidentViewSamples.Id);

        var elements = harness.Elements();
        var marked = elements.Where(element => element.Classes.Any(name => name.StartsWith("mark-", StringComparison.Ordinal))).ToArray();

        // Assert
        Assert.IsTrue(HasMarkedRow(elements, "mark-oom", "row-oom"), "the oom kill row is marked as an oom kill");
        Assert.IsTrue(HasMarkedRow(elements, "mark-boot", "row-boot"), "the boot row is marked as a boot");
        Assert.IsTrue(HasMarkedRow(elements, "mark-backup", "row-first"), "the row of the backup first seen is marked as a backup");
        Assert.IsTrue(HasMarkedRow(elements, "mark-backup", "row-last"), "the row of the backup last seen is marked as a backup");
        Assert.AreSequenceEqual(_markClasses, marked.SelectMany(element => element.Classes).Where(name => name.StartsWith("mark-", StringComparison.Ordinal)).Distinct().Order().ToArray(), "the three mark classes of the fixed map and no other");

        foreach (var token in _unmarkedRows)
        {
            Assert.DoesNotContain(element => element.Text.Contains(token, StringComparison.Ordinal), marked, $"the row {token} has no mark class");
        }

        Assert.Contains(Enc(quoted), harness.Html(), "a type with quotes is shown as text");
        Assert.Contains(Enc(markup), harness.Html(), "a type with markup is shown as text");
        Assert.DoesNotContain(markup, harness.Html(), "no markup from a type");
        Assert.IsTrue(elements.All(element => element.Tag != "b" && element.Tag != "script"), "no element of external markup");
        Assert.IsTrue(elements.All(element => element.Attributes.Keys.All(name => name is not ("onclick" or "style"))), "no element carries an attribute that external text could have set");
    }

    /// <summary>
    /// A number without an incident, or one that is not positive, shows the text that says so and reads no window.
    /// </summary>
    /// <param name="id">The number in the address</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(7L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    public async Task IncidentPageShowsUnknownIncident(long id)
    {
        // Arrange
        await using var harness = new IncidentPageHarness($"http://localhost/incidents/{id}");

        harness.Browser.IncidentResult = null;

        // Act
        await harness.AddAsync<IncidentPage>(id);

        // Assert
        var html = harness.Html();

        Assert.Contains(Enc(IncidentsView.UnknownIncident), html, "the text of an unknown incident");
        Assert.DoesNotContain("web-1", html, "no incident is shown");
        Assert.IsEmpty(harness.Records.Queries, "no window is read");
        Assert.IsEmpty(harness.Elements().Where(element => element.Tag == "form").ToArray(), "no note form");
    }

    /// <summary>
    /// External text is written only as escaped text: no markup, no direction character, invisible characters as visible escapes, and a note stays text inside its text area.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentPageWritesExternalTextOnlyAsEscapedText()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);
        var hostile = $"<script>alert(1)</script>{bidi}end";
        var note = "</textarea><script>x</script>";
        var stored = IncidentViewSamples.Full();

        stored.Incident.Kills[0].Host = hostile;
        stored.Incident.Kills[0].VictimCommand = hostile;
        stored.Incident.Timeline[0].Type = hostile;
        stored.Incident.Timeline[0].Host = hostile;
        stored.Incident.Timeline[0].Texts = new Dictionary<string, string>
                                            {
                                                [hostile] = hostile
                                            };
        stored.Incident.Hypothesis.Growth[0].Program = hostile;
        stored.Incident.Hypothesis.Consumers[0].Program = hostile;
        stored.Incident.Hypothesis.BackupPrograms = [hostile];
        stored.Incident.Connections!.Processes[0].Command = hostile;
        stored.Incident.Metrics[0].Name = hostile;
        stored.Incident.Metrics[0].Source = hostile;
        stored.Incident.NewTemplates[0].Template = hostile;
        stored.Incident.NewTemplates[0].Program = hostile;
        stored.Incident.NewTemplates[0].Source = hostile;
        stored.Incident.TemplateSpikes[0].Template = hostile;

        await using var harness = Open(stored);

        harness.Browser.NotesResult = new IncidentNotePage
                                      {
                                          Notes = [IncidentViewSamples.Note(IncidentViewSamples.Id, IncidentViewSamples.Id, note)]
                                      };

        // Act
        await harness.AddAsync<IncidentPage>(IncidentViewSamples.Id);

        var html = harness.Html();
        var elements = harness.Elements();
        var area = elements.Single(element => element.Tag == "textarea");

        // Assert
        Assert.Contains(bidi, hostile, "the input holds U+202E");
        Assert.DoesNotContain("<script>", html, "no markup from external text");
        Assert.DoesNotContain(bidi, html, "no direction character in the page");
        Assert.Contains(Enc("<script>alert(1)</script>"), html, "the markup is shown as text");
        Assert.Contains(element => element.Classes.Contains("escape") && element.Text == string.Concat((char)92, "u202e"), elements, "the direction character is a visible escape");
        Assert.IsTrue(elements.All(element => element.Tag != "script"), "no script element");
        Assert.AreEqual(note, area.Text.Length > 0 ? area.Text : area.Attributes.GetValueOrDefault("value"), "the note is text in the text area, whatever it contains");
        Assert.DoesNotContain("</textarea><script>", html, "the note cannot end its text area");
    }

    /// <summary>
    /// Every time of the page links to the logs of that moment: the kills, the first and last kill, the timeline entries, the last boot, the metric extremes, the connection peak, the backup activity and the templates.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentPageLinksEveryTimeToTheLogsOfThatMoment()
    {
        // Arrange
        await using var harness = Open(IncidentViewSamples.Full());

        // Act
        await harness.AddAsync<IncidentPage>(IncidentViewSamples.Id);

        // Assert
        foreach (var time in IncidentViewSamples.LinkedTimes())
        {
            var links = harness.Links(IncidentsView.LogsHref(time));

            Assert.IsNotEmpty(links, $"{LogsView.ShowSeconds(time)} links to the logs of that moment");
            Assert.Contains(link => link.Text.Contains(LogsView.ShowSeconds(time), StringComparison.Ordinal), links, $"the link shows {LogsView.ShowSeconds(time)}");
        }
    }

    /// <summary>
    /// The memory chart is a stacked chart with the fixed title, the program names escaped as labels, one column per series with the values in MiB, and the page names the cut of a long window; the other two charts are line charts with their fixed titles.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentPageRendersTheMemoryChartWithFixedTitleAndStackedData()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);
        var evil = $"ev{bidi}il";
        var stored = IncidentViewSamples.Full();

        stored.Incident.End = IncidentViewSamples.Start.AddHours(6);

        await using var harness = Open(stored);

        for (var index = 0; index < 305; index++)
        {
            var at = IncidentViewSamples.Start.AddMinutes(index);

            harness.Records.AddProcesses("legacy-monitor", at, (evil, 900 * Mib), ("p2", 80 * Mib), ("p3", 70 * Mib), ("p4", 60 * Mib), ("p5", 50 * Mib), ("p6", 40 * Mib), ("p7", 30 * Mib));
        }

        for (var index = 0; index < 3; index++)
        {
            var at = IncidentViewSamples.Start.AddMinutes(index);

            harness.Records.AddMetric("proc.meminfo", at, "mem_available_bytes", 100d * Mib);
            harness.Records.AddMetric("proc.meminfo", at, "swap_used_bytes", 10d * Mib);
            harness.Records.AddConnections("proc.net", at, 5, 6);
        }

        var cut = Enc(string.Format(CultureInfo.InvariantCulture, _chartCut, IncidentsView.MaxChartSnapshots, LogsView.ShowSeconds(IncidentViewSamples.Start.AddMinutes(299))));

        // Act
        await harness.AddAsync<IncidentPage>(IncidentViewSamples.Id);
        await harness.WaitForHtmlAsync(html => html.Contains(cut, StringComparison.Ordinal));

        var stacked = harness.Runtime.Calls.Where(call => call.Identifier == "createStacked").ToArray();
        var lines = harness.Runtime.Calls.Where(call => call.Identifier == "create").ToArray();
        var hosts = harness.Elements().Where(element => element.Classes.Contains("chart")).ToArray();

        // Assert
        Assert.Contains(bidi, evil, "the input holds U+202E");
        Assert.HasCount(1, stacked, "one stacked chart");
        Assert.AreEqual(IncidentsView.MemoryChartTitle, stacked[0].Arguments[1], "the fixed title of the memory chart");
        Assert.IsInstanceOfType<string[]>(stacked[0].Arguments[2], "the labels are an array of strings");
        Assert.IsInstanceOfType<double?[][]>(stacked[0].Arguments[3], "the columns are arrays of nullable numbers");

        var labels = (string[])stacked[0].Arguments[2]!;
        var columns = (double?[][])stacked[0].Arguments[3]!;

        Assert.AreEqual(LogText.Escape(evil, IncidentsView.MaxLabelChars), labels[0], "the label is the program name, escaped");
        Assert.AreEqual(IncidentsView.OtherPrograms, labels[^1], "the last series is Other programs");
        Assert.DoesNotContain(label => label.Contains(bidi, StringComparison.Ordinal), labels, "no direction character in a label");
        Assert.HasCount(IncidentsView.ChartPrograms + 1, labels, "five programs and the others");
        Assert.DoesNotContain(call => call.Arguments.OfType<string>().Any(text => text.Contains(bidi, StringComparison.Ordinal)), harness.Runtime.Calls, "no direction character reaches the script");
        Assert.HasCount(labels.Length + 1, columns, "a column of times and one per series");
        Assert.HasCount(IncidentsView.MaxChartSnapshots, columns[0], "300 points");
        Assert.AreEqual(900d, columns[1][0], "the largest program in MiB");
        Assert.AreEqual(70d, columns[^1][0], "the others in MiB: 40 plus 30");
        Assert.HasCount(2, lines, "the memory and swap chart and the connections chart");
        Assert.AreEqual(IncidentsView.MemorySwapChartTitle, lines[0].Arguments[1], "the fixed title of the memory and swap chart");
        Assert.AreEqual(IncidentsView.ConnectionsChartTitle, lines[1].Arguments[1], "the fixed title of the connections chart");
        Assert.AreSequenceEqual(new[] { IncidentsView.MemoryChartTitle, IncidentsView.MemorySwapChartTitle, IncidentsView.ConnectionsChartTitle }.Order().ToArray(), hosts.Select(element => element.Attributes["aria-label"]).Order().ToArray(), "three chart hosts labelled with the fixed titles");
    }

    /// <summary>
    /// A long window of connection snapshots is cut at 300, and the page names the time of the cut.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentPageNamesTheCutOfTheConnectionChart()
    {
        // Arrange
        var stored = IncidentViewSamples.Full();

        stored.Incident.End = IncidentViewSamples.Start.AddHours(6);

        await using var harness = Open(stored);

        for (var index = 0; index < 305; index++)
        {
            harness.Records.AddConnections("proc.net", IncidentViewSamples.Start.AddMinutes(index), 5, 6);
        }

        var cut = Enc(string.Format(CultureInfo.InvariantCulture, _chartCut, IncidentsView.MaxChartConnections, LogsView.ShowSeconds(IncidentViewSamples.Start.AddMinutes(299))));

        // Act
        await harness.AddAsync<IncidentPage>(IncidentViewSamples.Id);
        await harness.WaitForHtmlAsync(html => html.Contains(cut, StringComparison.Ordinal));

        // Assert
        Assert.Contains(cut, harness.Html(), "the cut of the connection chart");
        Assert.DoesNotContain(Enc(IncidentsView.NoWindowData), harness.Html(), "a chart was drawn");
    }

    /// <summary>
    /// The page has one form for the own note and one for each note that was moved to the incident, each a post to the notes address with the antiforgery token, the number it was written for and a text area of at most 1,800 characters, and it says when notes are not shown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentPageShowsNoteFormsWithAntiforgeryToken()
    {
        // Arrange
        await using var merged = Open(IncidentViewSamples.Full());
        await using var own = Open(IncidentViewSamples.Full());

        merged.Browser.NotesResult = new IncidentNotePage
                                     {
                                         Notes = [IncidentViewSamples.Note(7, 3, "merged three"), IncidentViewSamples.Note(7, 4, "merged four")],
                                         More = true
                                     };
        own.Browser.NotesResult = new IncidentNotePage
                                  {
                                      Notes = [IncidentViewSamples.Note(7, 7, "own text")]
                                  };

        // Act
        await merged.AddAsync<IncidentPage>(IncidentViewSamples.Id);
        await own.AddAsync<IncidentPage>(IncidentViewSamples.Id);

        var mergedForms = NoteForms(merged);
        var ownForms = NoteForms(own);

        // Assert
        Assert.HasCount(3, mergedForms, "the own note without a stored text and the two moved notes");
        Assert.AreSequenceEqual(_writtenForNumbers, mergedForms.Select(form => Hidden(form, "written_for")).Order().ToArray(), "one form for each number the notes were written for");
        Assert.HasCount(1, ownForms, "only the own note");
        Assert.AreEqual("7", Hidden(ownForms[0], "written_for"), "the own note is written for the incident");
        Assert.AreEqual("own text", TextOf(ownForms[0]), "the stored text of the own note");

        foreach (var form in mergedForms.Concat(ownForms))
        {
            var area = form.Descendants().Single(element => element.Tag == "textarea");

            Assert.AreEqual("post", form.Attributes["method"], "a plain post");
            Assert.AreEqual(IncidentsView.NoteAction(7), form.Attributes["action"], "to the notes address of the incident");
            Assert.AreEqual(FixedAntiforgeryStateProvider.TokenValue, Hidden(form, FixedAntiforgeryStateProvider.FormFieldName), "with the antiforgery token");
            Assert.AreEqual("note", area.Attributes["name"], "the text area is the field note");
            Assert.AreEqual("1800", area.Attributes["maxlength"], "of at most 1,800 characters");
            Assert.Contains(element => element.Tag == "button" && element.Text.Trim() == IncidentsView.SaveNote, form.Descendants().ToArray(), "with the save button");
        }

        Assert.AreEqual("merged three", TextOf(mergedForms.Single(form => Hidden(form, "written_for") == "3")), "the moved note of incident 3");
        Assert.AreEqual(string.Empty, TextOf(mergedForms.Single(form => Hidden(form, "written_for") == "7")), "the own note without a stored text is empty");
        Assert.Contains(Enc(string.Format(CultureInfo.InvariantCulture, _writtenFor, 3L)), merged.Html(), "the moved note says which incident it was written for");
        Assert.Contains(Enc(string.Format(CultureInfo.InvariantCulture, _writtenFor, 4L)), merged.Html(), "the second moved note says which incident it was written for");
        Assert.DoesNotContain(Enc(string.Format(CultureInfo.InvariantCulture, _writtenFor, 7L)), merged.Html(), "the own note has no such label");
        Assert.Contains(Enc(IncidentsView.NotesHidden), merged.Html(), "more notes than are shown");
        Assert.DoesNotContain(Enc(IncidentsView.NotesHidden), own.Html(), "no such text without more notes");
    }

    /// <summary>
    /// The read of the incident and of its notes, and the read of the window, each stop at the deadline with their text; a stuck window read leaves the rest of the page.
    /// </summary>
    /// <param name="blocked">What is stuck: the incident, the notes or the window</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("incident")]
    [DataRow("notes")]
    [DataRow("window")]
    public async Task IncidentPageStopsReadsAtTheDeadline(string blocked)
    {
        // Arrange
        await using var harness = Open(IncidentViewSamples.Full());

        var method = blocked switch
                     {
                         "incident" => FakeIncidentBrowser.Incident,
                         "notes" => FakeIncidentBrowser.IncidentNotes,
                         _ => string.Empty
                     };
        var window = method.Length == 0;

        harness.Browser.Before = (called, token) => called == method ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        harness.Records.Before = (_, token) => window ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;

        // Act
        var add = harness.AddAsync<IncidentPage>(IncidentViewSamples.Id);

        await (window ? harness.Records.WaitForCallsAsync(1) : harness.Browser.WaitForCallsAsync(method, 1));

        harness.Clock.Advance(LogsView.Deadline);

        var text = Enc(window ? IncidentsView.WindowDeadline : IncidentsView.ListDeadline);

        await harness.WaitForHtmlAsync(html => html.Contains(text, StringComparison.Ordinal));
        await add.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var shown = harness.Html();
        var token = window ? harness.Records.Tokens[0] : harness.Browser.Calls.First(call => call.Method == method).CancellationToken;

        Assert.Contains(text, shown, "the deadline text of the read");
        Assert.IsTrue(token.IsCancellationRequested, "the read was cancelled");
        Assert.AreEqual(window, shown.Contains(Enc(IncidentsView.WindowDeadline), StringComparison.Ordinal), "the text of the window deadline appears only for a stuck window read");

        if (window)
        {
            Assert.Contains("web-1", shown, "the rest of the page stays");
            Assert.DoesNotContain(Enc(IncidentsView.ListDeadline), shown, "the incident was read");
        }
    }

    /// <summary>
    /// Asserts that a label of the page is followed, within its own entry, by a value.
    /// </summary>
    /// <param name="html">The markup of the page</param>
    /// <param name="label">The label</param>
    /// <param name="value">The value</param>
    /// <param name="what">What is checked</param>
    private static void AssertLabelled(string html, string label, string value, string what)
    {
        var index = html.IndexOf(label, StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, index, $"the label {label} is shown: {what}");
        Assert.Contains(Enc(value), html[index..Math.Min(html.Length, index + 300)], $"the value follows the label {label}: {what}");
    }

    /// <summary>
    /// Creates a harness whose browser answers a stored incident, and without notes.
    /// </summary>
    /// <param name="stored">The incident</param>
    /// <returns>The harness</returns>
    private static IncidentPageHarness Open(StoredIncident stored)
    {
        return new IncidentPageHarness(Address)
               {
                   Browser =
                   {
                       IncidentResult = stored
                   }
               };
    }

    /// <summary>
    /// Makes the hypothesis of an incident unknown.
    /// </summary>
    /// <param name="stored">The incident</param>
    /// <returns>The same incident</returns>
    private static StoredIncident Unknown(StoredIncident stored)
    {
        stored.Incident.Hypothesis = new IncidentHypothesis
                                     {
                                         GrowthKnown = false,
                                         BackupRunning = null,
                                         RecentBootAt = null
                                     };

        return stored;
    }

    /// <summary>
    /// Tells whether an element that holds one row of the page, and no other row, has a mark class.
    /// </summary>
    /// <param name="elements">The elements of the page</param>
    /// <param name="mark">The class</param>
    /// <param name="token">A text that only this row holds; every row holds exactly one text that starts with <c>row-</c></param>
    /// <returns><c>true</c> when such an element exists</returns>
    private static bool HasMarkedRow(IReadOnlyList<RenderedElement> elements, string mark, string token)
    {
        return elements.Any(element => element.Classes.Contains(mark) && element.Text.Contains(token, StringComparison.Ordinal) && element.Text.Split("row-").Length == 2);
    }

    /// <summary>
    /// Returns the note forms of a page.
    /// </summary>
    /// <param name="harness">The harness</param>
    /// <returns>The forms</returns>
    private static RenderedElement[] NoteForms(IncidentPageHarness harness)
    {
        return harness.Elements().Where(element => element.Tag == "form" && element.Attributes.GetValueOrDefault("action") == IncidentsView.NoteAction(7)).ToArray();
    }

    /// <summary>
    /// Returns the value of a hidden field of a form.
    /// </summary>
    /// <param name="form">The form</param>
    /// <param name="name">The name of the field</param>
    /// <returns>The value</returns>
    private static string Hidden(RenderedElement form, string name)
    {
        var field = form.Descendants().Single(element => element.Tag == "input" && element.Attributes.GetValueOrDefault("name") == name);

        Assert.AreEqual("hidden", field.Attributes.GetValueOrDefault("type"), $"the field {name} is hidden");

        return field.Attributes.GetValueOrDefault("value") ?? string.Empty;
    }

    /// <summary>
    /// Returns the text in the text area of a form.
    /// </summary>
    /// <param name="form">The form</param>
    /// <returns>The text</returns>
    private static string TextOf(RenderedElement form)
    {
        var area = form.Descendants().Single(element => element.Tag == "textarea");

        return area.Text.Length > 0 ? area.Text : area.Attributes.GetValueOrDefault("value") ?? string.Empty;
    }

    #endregion // Methods
}