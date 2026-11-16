using System.Globalization;

using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Backend.Overview;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="OverviewView"/>
/// </summary>
[TestClass]
public class OverviewViewTests
{
    #region Constants

    /// <summary>
    /// The text of the last live data without an agent (spec E1).
    /// </summary>
    private const string NoLiveData = "No live data has arrived yet. Install the agent, or import logs as the README describes.";

    /// <summary>
    /// The text of the services without a stored state (spec E1).
    /// </summary>
    private const string NoServices = "No service state is stored.";

    /// <summary>
    /// The text of the incidents without a stored incident (spec E1).
    /// </summary>
    private const string NoIncidents = "No incidents are stored. Run vandoxd incidents after an import.";

    /// <summary>
    /// The text of the incidents when none is open (AC6).
    /// </summary>
    private const string NoOpenIncident = "No open incident.";

    /// <summary>
    /// The text of a missing value.
    /// </summary>
    private const string Unknown = "unknown";

    /// <summary>
    /// The mark appended to a freshness or spool text that is stale.
    /// </summary>
    private const string StaleMark = " stale";

    /// <summary>
    /// The size of one GiB in bytes.
    /// </summary>
    private const double GiB = 1073741824;

    /// <summary>
    /// The size of one MiB in bytes.
    /// </summary>
    private const double MiB = 1048576;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the time the model is built at.
    /// </summary>
    private static DateTimeOffset Now => new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The last live data and the last data received show their time and their age.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void BuildShowsLiveDataAndReceivedTimeWithAges()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-45), Now.AddMinutes(-2));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("2026-10-01 09:59:15 (45 s ago)", model.LiveData, "the last live data with its age");
        Assert.AreEqual("2026-10-01 09:58:00 (2 min ago)", model.Received, "the last data received with its age");
    }

    /// <summary>
    /// The last live data is marked stale when it is older than 5 minutes, and a time exactly 5 minutes old is not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void BuildMarksLiveDataStaleAfterFiveMinutes()
    {
        // Arrange
        var data = Fresh(Now.AddMinutes(-5).AddSeconds(-1), Now.AddMinutes(-5));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("2026-10-01 09:54:59 (5 min ago)" + StaleMark, model.LiveData, "the live data older than 5 minutes is stale");
        Assert.AreEqual("2026-10-01 09:55:00 (5 min ago)", model.Received, "a receive time exactly 5 minutes old is not stale");
    }

    /// <summary>
    /// Without a live record among the scanned ones the last live data shows the oldest scanned time as a bound, marked stale.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void BuildShowsBeforeWhenNoLiveRecordWasScanned()
    {
        // Arrange
        var data = Fresh(null, Now.AddSeconds(-10));

        data.Freshness = new AgentFreshness
                         {
                             AgentId = "agent-1",
                             ReceivedAt = Now.AddSeconds(-10),
                             ScannedBackTo = Now.AddHours(-3)
                         };

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("before 2026-10-01 07:00:00" + StaleMark, model.LiveData, "the oldest scanned time as a bound");
    }

    /// <summary>
    /// A time after now shows its age as ahead by that age and is not stale.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void BuildShowsAheadByForTimeAfterNow()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(80), Now.AddSeconds(5));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("2026-10-01 10:01:20 (ahead by 1 min)", model.LiveData, "a live time ahead of now");
        Assert.AreEqual("2026-10-01 10:00:05 (ahead by 5 s)", model.Received, "a receive time ahead of now");
    }

    /// <summary>
    /// Without any agent the page shows the texts of an empty store: no live data, no service state, every key figure unknown and no incidents.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    [TestCategory("AC4")]
    [TestCategory("AC5")]
    [TestCategory("AC6")]
    public void BuildShowsNoLiveDataTextWithoutAgent()
    {
        // Arrange
        var data = new OverviewData();

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual(NoLiveData, model.LiveData, "the text of an empty store for the live data");
        Assert.AreEqual(Unknown, model.Spool, "no spool sample");
        Assert.AreEqual(NoServices, model.ServicesText, "the text of an empty store for the services");
        Assert.IsEmpty(model.Units, "no unit rows");
        Assert.IsNotEmpty(model.Figures, "the key figures are listed");
        Assert.AreEqual(string.Empty, string.Join(", ", model.Figures.Where(row => row.Value != Unknown).Select(row => row.Figure)), "every key figure is unknown");
        Assert.AreEqual(NoIncidents, model.IncidentsText, "the text of an empty store for the incidents");
        Assert.IsNull(model.LatestIncident, "no latest incident");
    }

    /// <summary>
    /// An agent whose newest records were removed shows the last data received as unknown and stale, not the text of an empty store.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void BuildShowsReceivedUnknownForAgentWithoutRecords()
    {
        // Arrange
        var data = new OverviewData
                   {
                       Freshness = new AgentFreshness
                                   {
                                       AgentId = "agent-1"
                                   }
                   };

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual(Unknown + StaleMark, model.Received, "the last data received is unknown and stale");
        Assert.AreNotEqual(NoLiveData, model.LiveData, "a known agent is not shown as an empty store");
    }

    /// <summary>
    /// With open sequence gaps and a batch received within 5 minutes the backfill shows the span of the gaps, without any count.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void BuildShowsBackfillRunningWithSpanAndNoCount()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Freshness = Gaps(data.Freshness, 2);

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("Backfill running: data from 2026-10-01 07:00:00 to 2026-10-01 09:00:00 is missing.", model.Backfill, "the span of the open gaps without a count");
    }

    /// <summary>
    /// With open sequence gaps but no batch for more than 5 minutes the backfill shows the stalled form with the last receive time.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void BuildShowsBackfillStalledWithoutDataForFiveMinutes()
    {
        // Arrange
        var data = Fresh(Now.AddMinutes(-7), Now.AddMinutes(-6));

        data.Freshness = Gaps(data.Freshness, 1);

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("Backfill stalled: data from 2026-10-01 07:00:00 to 2026-10-01 09:00:00 is missing, no data received since 2026-10-01 09:54:00.", model.Backfill, "the stalled form");
    }

    /// <summary>
    /// Without an open sequence gap the backfill says that none runs.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void BuildShowsNoBackfillRunningWithoutOpenGap()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("No backfill running.", model.Backfill, "no open gap");
    }

    /// <summary>
    /// The spool shows its size, the time of its oldest unsent record (sample time minus its age) and the sample time.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void BuildShowsSpoolSizeAndOldestUnsentRecord()
    {
        // Arrange
        var data = Spooled(Now.AddSeconds(-30), 1200);

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("1.5 MiB, oldest unsent record 2026-10-01 09:39:30, as of 2026-10-01 09:59:30", model.Spool, "size, oldest unsent record and sample time");
    }

    /// <summary>
    /// A spool age of 0 shows that nothing is unsent.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void BuildShowsNothingUnsentForAgeZero()
    {
        // Arrange
        var data = Spooled(Now.AddSeconds(-30), 0);

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("1.5 MiB, nothing unsent, as of 2026-10-01 09:59:30", model.Spool, "nothing unsent");
    }

    /// <summary>
    /// A spool sample older than 5 minutes is marked stale.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void BuildMarksSpoolSampleStale()
    {
        // Arrange
        var data = Spooled(Now.AddMinutes(-6), 0);

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("1.5 MiB, nothing unsent, as of 2026-10-01 09:54:00" + StaleMark, model.Spool, "the stale spool sample");
    }

    /// <summary>
    /// Without a spool sample the spool is unknown.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void BuildShowsSpoolUnknownWithoutSample()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual(Unknown, model.Spool, "no spool sample");
    }

    /// <summary>
    /// The units keep the order of the read, non-active first; each row shows its state, the time it became active, its restarts and its time, and a failed unit is marked.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void BuildListsNonActiveUnitsFirstAndMarksFailed()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Services = Services(2,
                                 Unit("x.service", "failed", "failed", "2026-10-01T06:00:00+00:00", 4, Now.AddSeconds(-30)),
                                 Unit("a.service", "active", "running", "not a time", 0, Now.AddSeconds(-30)),
                                 Unit("b.service", "active", "running", null, 1, Now.AddSeconds(-30)));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual(string.Empty, model.ServicesText, "rows are shown");
        Assert.AreEqual(string.Empty, model.ServicesStale, "a batch arrived within 5 minutes");
        Assert.AreEqual("x.service|failed (failed)|2026-10-01 06:00:00|4|2026-10-01 09:59:30|failed\n"
                        + "a.service|active (running)|unknown|0|2026-10-01 09:59:30|\n"
                        + "b.service|active (running)|unknown|1|2026-10-01 09:59:30|",
                        DescribeUnits(model.Units),
                        "the rows in order with state, active since, restarts, time and the failed mark");
    }

    /// <summary>
    /// The services are marked stale with the last receive time when no batch arrived for more than 5 minutes, and are not at exactly 5 minutes.
    /// </summary>
    /// <param name="secondsAgo">The age of the last receive time in seconds</param>
    /// <param name="expected">The expected stale text</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow(301, "stale: no data received since 2026-10-01 09:54:59")]
    [DataRow(300, "")]
    public void BuildMarksServicesStaleWithoutBatchForFiveMinutes(int secondsAgo, string expected)
    {
        // Arrange
        var data = Fresh(Now.AddMinutes(-10), Now.AddSeconds(-secondsAgo));

        data.Services = Services(1, Unit("a.service", "active", "running", null, 0, Now.AddMinutes(-10)));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual(expected, model.ServicesStale, $"the stale text after {secondsAgo} seconds without a batch");
    }

    /// <summary>
    /// A unit whose latest record is more than 1 hour 5 minutes old is marked stale, one exactly that old is not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void BuildMarksUnitStaleAfterOneHourFiveMinutes()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Services = Services(2,
                                 Unit("b.service", "active", "running", null, 0, Now.AddMinutes(-65).AddSeconds(-1)),
                                 Unit("c.service", "active", "running", null, 0, Now.AddMinutes(-65)));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("b.service|active (running)|unknown|0|2026-10-01 08:54:59|stale\n"
                        + "c.service|active (running)|unknown|0|2026-10-01 08:55:00|",
                        DescribeUnits(model.Units),
                        "only the unit older than 1 hour 5 minutes is stale");
    }

    /// <summary>
    /// Beyond the cap of 256 units the rest is counted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void BuildShowsMoreUnitsBeyondTheCap()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));
        var units = Enumerable.Range(0, OverviewView.MaxUnits)
                              .Select(index => Unit(string.Create(CultureInfo.InvariantCulture, $"u{index:D3}.service"), "active", "running", null, 0, Now.AddSeconds(-30)))
                              .ToArray();

        data.Services = Services(300, units);

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.HasCount(OverviewView.MaxUnits, model.Units, "at most 256 unit rows");
        Assert.AreEqual("and 44 more", model.MoreUnits, "the units beyond the cap");
    }

    /// <summary>
    /// The key figures show each value with its time in the fixed row order.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void BuildShowsKeyFiguresWithTimes()
    {
        // Arrange
        var data = Figures(Now.AddSeconds(-30));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("Available memory||2.0 GiB (25.0 % of 8.0 GiB)|2026-10-01 09:59:30|\n"
                        + "Swap||1.0 GiB of 4.0 GiB used (25.0 %)|2026-10-01 09:59:30|\n"
                        + "Memory pressure||some 1.5 %, full 0.5 %|2026-10-01 09:59:30|\n"
                        + "CPU pressure||some 12.4 %|2026-10-01 09:59:30|\n"
                        + "I/O pressure||some 3.0 %, full 2.0 %|2026-10-01 09:59:30|\n"
                        + "Disk|/|10.0 GiB available (25.0 %) of 40.0 GiB|2026-10-01 09:59:30|\n"
                        + "MariaDB||up|2026-10-01 09:59:30|\n"
                        + "MariaDB connections||12 of 151|2026-10-01 09:59:30|\n"
                        + "MariaDB memory||512.0 MiB of at most 2.0 GiB|2026-10-01 09:59:30|",
                        DescribeFigures(model.Figures),
                        "every key figure with its value and time");
        Assert.AreEqual(string.Empty, model.MoreMounts, "every mount is shown");
    }

    /// <summary>
    /// A key figure older than 5 minutes is marked stale, one exactly 5 minutes old is not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void BuildMarksFigureStaleAfterFiveMinutes()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Values = [
                          Series("system", "mem_available_bytes", Value(Now.AddMinutes(-5).AddSeconds(-1), 2 * GiB)),
                          Series("system", "mem_total_bytes", Value(Now.AddMinutes(-5).AddSeconds(-1), 8 * GiB)),
                          Series("system", "swap_used_bytes", Value(Now.AddMinutes(-5), GiB)),
                          Series("system", "swap_total_bytes", Value(Now.AddMinutes(-5), 4 * GiB))
                      ];

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("Available memory||2.0 GiB (25.0 % of 8.0 GiB)|2026-10-01 09:54:59|stale\n"
                        + "Swap||1.0 GiB of 4.0 GiB used (25.0 %)|2026-10-01 09:55:00|",
                        DescribeFigures(model.Figures.Where(row => row.Figure is "Available memory" or "Swap")),
                        "only the value older than 5 minutes is stale");
    }

    /// <summary>
    /// A figure the latest values lack shows unknown, never 0; MariaDB shows its availability and its other figures unknown when it is not up.
    /// </summary>
    /// <param name="availability">The stored availability</param>
    /// <param name="shown">The text of the availability</param>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("down", "down")]
    [DataRow("not_answering", "not answering")]
    public void BuildShowsUnknownForMissingFigure(string availability, string shown)
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.MariaDb = new MariaDbLatest
                       {
                           CapturedAt = Now.AddSeconds(-30),
                           Availability = availability
                       };

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual(shown, ValueOf(model, "MariaDB"), "the availability of MariaDB");
        Assert.AreEqual(Unknown, ValueOf(model, "MariaDB connections"), "connections of a server that is not up");
        Assert.AreEqual(Unknown, ValueOf(model, "MariaDB memory"), "memory of a server that is not up");
        Assert.AreEqual(Unknown, ValueOf(model, "Available memory"), "missing available memory");
        Assert.AreEqual(Unknown, ValueOf(model, "Swap"), "missing swap");
        Assert.AreEqual(Unknown, ValueOf(model, "Memory pressure"), "missing PSI");
        Assert.AreEqual(Unknown, ValueOf(model, "CPU pressure"), "missing PSI");
        Assert.AreEqual(Unknown, ValueOf(model, "I/O pressure"), "missing PSI");
    }

    /// <summary>
    /// A swap total of 0 shows no swap, and a share whose total is 0 shows no percentage.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void BuildShowsNoSwapAndNoShareForZeroTotal()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Values = [
                          Series("system", "mem_available_bytes", Value(Now.AddSeconds(-30), 2 * GiB)),
                          Series("system", "mem_total_bytes", Value(Now.AddSeconds(-30), 0)),
                          Series("system", "swap_used_bytes", Value(Now.AddSeconds(-30), 0)),
                          Series("system", "swap_total_bytes", Value(Now.AddSeconds(-30), 0)),
                          Series("system", "filesystem_available_bytes", Value(Now.AddSeconds(-30), 10 * GiB, "/")),
                          Series("system", "filesystem_size_bytes", Value(Now.AddSeconds(-30), 0, "/"))
                      ];

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("no swap", ValueOf(model, "Swap"), "a swap total of 0");
        Assert.AreEqual("2.0 GiB", ValueOf(model, "Available memory"), "no share of a memory total of 0");
        Assert.AreEqual("10.0 GiB available", ValueOf(model, "Disk"), "no share of a file system size of 0");
    }

    /// <summary>
    /// The mounts are shown up to the cap of 16 in the order of the read, each as external text, and the rest is counted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void BuildShowsMountsUpToTheCapWithMore()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));
        var mounts = Enumerable.Range(0, OverviewView.MaxMounts).Select(index => string.Create(CultureInfo.InvariantCulture, $"/m{index:D2}")).ToList();

        data.Values = [
                          new LatestSeries
                          {
                              Series = new SeriesKey("system", "filesystem_available_bytes"),
                              Values = mounts.Select(mount => Value(Now.AddSeconds(-30), GiB, mount)).ToList(),
                              LabelSets = 20
                          },
                          new LatestSeries
                          {
                              Series = new SeriesKey("system", "filesystem_size_bytes"),
                              Values = mounts.Select(mount => Value(Now.AddSeconds(-30), 4 * GiB, mount)).ToList(),
                              LabelSets = 20
                          }
                      ];

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        var disks = model.Figures.Where(row => row.Figure == "Disk").ToList();

        Assert.AreEqual(string.Join(", ", mounts), string.Join(", ", disks.Select(row => RunText(row.Mount))), "one row per mount up to the cap, in order");
        Assert.IsTrue(disks.All(row => row.Value == "1.0 GiB available (25.0 %) of 4.0 GiB"), "each mount with its available space and size");
        Assert.AreEqual("and 4 more mounts", model.MoreMounts, "the mounts beyond the cap");
    }

    /// <summary>
    /// A <c>max_connections</c> the store could not read as a non-negative integer (such as 5 followed by U+202E, refused by the store read) shows the connections as unknown.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void BuildShowsMaxConnectionsUnknownWhenNotAnInteger()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.MariaDb = new MariaDbLatest
                       {
                           CapturedAt = Now.AddSeconds(-30),
                           Availability = "up",
                           ThreadsConnected = 12,
                           MemoryUsed = 512 * MiB,
                           MaxConnections = null
                       };

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.AreEqual("up", ValueOf(model, "MariaDB"), "the availability");
        Assert.AreEqual(Unknown, ValueOf(model, "MariaDB connections"), "connections without a readable maximum");
    }

    /// <summary>
    /// An incident whose window has not ended and whose first kill is not after now is listed as open with its link and its victim as external text; it is also the latest incident.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void BuildListsOpenIncidentWithLink()
    {
        // Arrange
        var victim = $"mysqld{char.ConvertFromUtf32(0x202E)}x";
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Incidents = [
                             Incident(7, Now.AddMinutes(-50), Now.AddMinutes(30), victim),
                             Incident(6, Now.AddHours(-5), Now.AddHours(-4), "php-fpm")
                         ];

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        var expectedVictim = DescribeRuns(LogText.Runs(victim, LogText.MaxShortChars, IncidentsView.MaxTextRuns));

        Assert.HasCount(1, model.OpenIncidents, "one open incident");
        Assert.AreEqual($"7|incidents/7|2026-10-01 09:10:00|{expectedVictim}|", DescribeIncident(model.OpenIncidents[0]), "the open incident with link, first kill and victim as external text");
        Assert.Contains(run => run.Escaped, model.OpenIncidents[0].Victim, "the direction control of the victim is shown escaped");
        Assert.AreEqual(string.Empty, model.IncidentsText, "no text instead of the open incident");
        Assert.IsNotNull(model.LatestIncident, "the latest incident is shown");
        Assert.AreEqual(7, model.LatestIncident.Number, "the latest incident is the newest summary");
    }

    /// <summary>
    /// Without an open incident the page says so and shows the latest stored incident.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void BuildShowsNoOpenIncidentAndLatestIncident()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Incidents = [
                             Incident(5, Now.AddHours(-2), Now.AddHours(-1), "mysqld"),
                             Incident(4, Now.AddHours(-9), Now.AddHours(-8), "php-fpm")
                         ];

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.IsEmpty(model.OpenIncidents, "no open incident");
        Assert.AreEqual(NoOpenIncident, model.IncidentsText, "the text without an open incident");
        Assert.IsNotNull(model.LatestIncident, "the latest incident is shown");
        Assert.AreEqual("5|incidents/5|2026-10-01 08:00:00|mysqld:plain|", DescribeIncident(model.LatestIncident), "the latest incident with number, link, first kill and victim");
    }

    /// <summary>
    /// An incident whose first kill lies after now is never open, and as the latest incident it is marked as dated in the future.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void BuildNeverOpensIncidentWithKillAfterNow()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Incidents = [Incident(9, Now.AddHours(1), Now.AddHours(2), "mysqld")];

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.IsEmpty(model.OpenIncidents, "an incident with a first kill after now is not open");
        Assert.AreEqual(NoOpenIncident, model.IncidentsText, "the text without an open incident");
        Assert.IsNotNull(model.LatestIncident, "the latest incident is shown");
        Assert.AreEqual("9|incidents/9|2026-10-01 11:00:00|mysqld:plain|future", DescribeIncident(model.LatestIncident), "the latest incident marked as dated in the future");
    }

    /// <summary>
    /// Without any stored incident the page shows the text of an empty store and no latest incident.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void BuildShowsNoIncidentsText()
    {
        // Arrange
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        // Act
        var model = OverviewView.Build(data, Now);

        // Assert
        Assert.IsEmpty(model.OpenIncidents, "no open incident");
        Assert.AreEqual(NoIncidents, model.IncidentsText, "the text of an empty store");
        Assert.IsNull(model.LatestIncident, "no latest incident");
    }

    /// <summary>
    /// Creates the reads of an agent with a live time and a receive time and nothing else.
    /// </summary>
    /// <param name="lastLiveAt">The capture time of the newest live record, or <c>null</c> for none</param>
    /// <param name="receivedAt">The receive time of the newest batch</param>
    /// <returns>The reads</returns>
    private static OverviewData Fresh(DateTimeOffset? lastLiveAt, DateTimeOffset receivedAt)
    {
        return new OverviewData
               {
                   Freshness = new AgentFreshness
                               {
                                   AgentId = "agent-1",
                                   ReceivedAt = receivedAt,
                                   LastLiveAt = lastLiveAt
                               }
               };
    }

    /// <summary>
    /// Adds open sequence gaps from 3 hours to 1 hour before now to a freshness read.
    /// </summary>
    /// <param name="freshness">The freshness read</param>
    /// <param name="count">The number of open gaps</param>
    /// <returns>The freshness read with the gaps</returns>
    private static AgentFreshness Gaps(AgentFreshness freshness, int count)
    {
        return new AgentFreshness
               {
                   AgentId = freshness.AgentId,
                   ReceivedAt = freshness.ReceivedAt,
                   LastLiveAt = freshness.LastLiveAt,
                   OpenSequenceGaps = count,
                   MissingStart = Now.AddHours(-3),
                   MissingEnd = Now.AddHours(-1)
               };
    }

    /// <summary>
    /// Creates the reads of a fresh agent with a spool sample of 1.5 MiB.
    /// </summary>
    /// <param name="sampledAt">The time of the sample</param>
    /// <param name="ageSeconds">The age of the oldest unsent record in seconds</param>
    /// <returns>The reads</returns>
    private static OverviewData Spooled(DateTimeOffset sampledAt, double ageSeconds)
    {
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Values = [
                          Series("agent", "agent_spool_bytes", Value(sampledAt, 1.5 * MiB)),
                          Series("agent", "agent_spool_age_seconds", Value(sampledAt, ageSeconds))
                      ];

        return data;
    }

    /// <summary>
    /// Creates the reads of every key figure, all with one time.
    /// </summary>
    /// <param name="at">The time of the values</param>
    /// <returns>The reads</returns>
    private static OverviewData Figures(DateTimeOffset at)
    {
        var data = Fresh(Now.AddSeconds(-30), Now.AddSeconds(-10));

        data.Values = [
                          Series("system", "mem_available_bytes", Value(at, 2 * GiB)),
                          Series("system", "mem_total_bytes", Value(at, 8 * GiB)),
                          Series("system", "swap_used_bytes", Value(at, GiB)),
                          Series("system", "swap_total_bytes", Value(at, 4 * GiB)),
                          Series("system",
                                 "pressure_avg60",
                                 Pressure(at, "cpu", "some", 12.4),
                                 Pressure(at, "io", "full", 2),
                                 Pressure(at, "io", "some", 3),
                                 Pressure(at, "memory", "full", 0.5),
                                 Pressure(at, "memory", "some", 1.5)),
                          Series("system", "filesystem_available_bytes", Value(at, 10 * GiB, "/")),
                          Series("system", "filesystem_size_bytes", Value(at, 40 * GiB, "/")),
                          Series("mariadb", "mariadb_memory_max_bytes", Value(at, 2 * GiB))
                      ];
        data.MariaDb = new MariaDbLatest
                       {
                           CapturedAt = at,
                           Availability = "up",
                           ThreadsConnected = 12,
                           MemoryUsed = 512 * MiB,
                           MaxConnections = 151
                       };

        return data;
    }

    /// <summary>
    /// Creates the result of one series.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <param name="values">The values, one per label set</param>
    /// <returns>The series</returns>
    private static LatestSeries Series(string source, string name, params LatestValue[] values)
    {
        return new LatestSeries
               {
                   Series = new SeriesKey(source, name),
                   Values = values,
                   LabelSets = values.Length
               };
    }

    /// <summary>
    /// Creates a value without labels.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="value">The value</param>
    /// <returns>The value</returns>
    private static LatestValue Value(DateTimeOffset at, double value)
    {
        return new LatestValue
               {
                   CapturedAt = at,
                   Value = value
               };
    }

    /// <summary>
    /// Creates a value of a mount.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="value">The value</param>
    /// <param name="mount">The mount point</param>
    /// <returns>The value</returns>
    private static LatestValue Value(DateTimeOffset at, double value, string mount)
    {
        return new LatestValue
               {
                   CapturedAt = at,
                   Value = value,
                   Labels = new Dictionary<string, string>
                            {
                                ["mount"] = mount
                            }
               };
    }

    /// <summary>
    /// Creates a value of a pressure metric.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="resource">The resource</param>
    /// <param name="kind">The kind</param>
    /// <param name="value">The value in percent</param>
    /// <returns>The value</returns>
    private static LatestValue Pressure(DateTimeOffset at, string resource, string kind, double value)
    {
        return new LatestValue
               {
                   CapturedAt = at,
                   Value = value,
                   Labels = new Dictionary<string, string>
                            {
                                ["kind"] = kind,
                                ["resource"] = resource
                            }
               };
    }

    /// <summary>
    /// Creates the service read of units in the order the store returns them.
    /// </summary>
    /// <param name="total">The number of units in total</param>
    /// <param name="states">The returned units</param>
    /// <returns>The read</returns>
    private static ServiceStatePage Services(int total, params LatestServiceState[] states)
    {
        return new ServiceStatePage
               {
                   States = states,
                   Total = total,
                   Newest = states.Max(state => state.CapturedAt)
               };
    }

    /// <summary>
    /// Creates the latest state of a unit.
    /// </summary>
    /// <param name="unit">The unit</param>
    /// <param name="activeState">The active state</param>
    /// <param name="subState">The sub state</param>
    /// <param name="activeEnterAt">The stored time the unit became active, or <c>null</c> for none</param>
    /// <param name="restarts">The restart counter</param>
    /// <param name="at">The capture time</param>
    /// <returns>The state</returns>
    private static LatestServiceState Unit(string unit, string activeState, string subState, string? activeEnterAt, long restarts, DateTimeOffset at)
    {
        return new LatestServiceState
               {
                   Unit = unit,
                   ActiveState = activeState,
                   SubState = subState,
                   ActiveEnterAt = activeEnterAt,
                   Restarts = restarts,
                   CapturedAt = at
               };
    }

    /// <summary>
    /// Creates an incident summary whose window starts 10 minutes before its first kill.
    /// </summary>
    /// <param name="id">The number</param>
    /// <param name="firstKillAt">The time of the first kill</param>
    /// <param name="end">The end of the window</param>
    /// <param name="victim">The first victim</param>
    /// <returns>The summary</returns>
    private static IncidentSummary Incident(long id, DateTimeOffset firstKillAt, DateTimeOffset end, string victim)
    {
        return new IncidentSummary
               {
                   Id = id,
                   Start = firstKillAt.AddMinutes(-10),
                   End = end,
                   FirstKillAt = firstKillAt,
                   LastKillAt = firstKillAt,
                   KillCount = 1,
                   FirstVictim = victim
               };
    }

    /// <summary>
    /// Gets the value of the one figure row of a name.
    /// </summary>
    /// <param name="model">The model</param>
    /// <param name="figure">The name of the figure</param>
    /// <returns>The value text</returns>
    private static string ValueOf(OverviewModel model, string figure)
    {
        var rows = model.Figures.Where(row => row.Figure == figure).ToList();

        Assert.HasCount(1, rows, $"one row of {figure}");

        return rows[0].Value;
    }

    /// <summary>
    /// Joins the texts of runs.
    /// </summary>
    /// <param name="runs">The runs</param>
    /// <returns>The text</returns>
    private static string RunText(IReadOnlyList<TextRun> runs)
    {
        return string.Concat(runs.Select(run => run.Text));
    }

    /// <summary>
    /// Describes runs as their texts with a mark of escaped or plain.
    /// </summary>
    /// <param name="runs">The runs</param>
    /// <returns>The description</returns>
    private static string DescribeRuns(IReadOnlyList<TextRun> runs)
    {
        return string.Join(" ", runs.Select(run => run.Text + (run.Escaped ? ":escaped" : ":plain")));
    }

    /// <summary>
    /// Describes unit rows, one line each: unit, state, active since, restarts, time and the marks.
    /// </summary>
    /// <param name="rows">The rows</param>
    /// <returns>The description</returns>
    private static string DescribeUnits(IEnumerable<OverviewUnitRow> rows)
    {
        return string.Join("\n", rows.Select(row => string.Join("|", RunText(row.Unit), row.State, row.ActiveSince, row.Restarts, row.Recorded, Marks(row.Failed, "failed") + Marks(row.Stale, "stale"))));
    }

    /// <summary>
    /// Describes figure rows, one line each: figure, mount, value, time and the stale mark.
    /// </summary>
    /// <param name="rows">The rows</param>
    /// <returns>The description</returns>
    private static string DescribeFigures(IEnumerable<OverviewFigureRow> rows)
    {
        return string.Join("\n", rows.Select(row => string.Join("|", row.Figure, RunText(row.Mount), row.Value, row.Time, Marks(row.Stale, "stale"))));
    }

    /// <summary>
    /// Describes an incident row: number, link, first kill, victim runs and the future mark.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The description</returns>
    private static string DescribeIncident(OverviewIncidentRow row)
    {
        return string.Join("|", row.Number.ToString(CultureInfo.InvariantCulture), row.Href, row.FirstKill, DescribeRuns(row.Victim), Marks(row.Future, "future"));
    }

    /// <summary>
    /// Gets a mark when a flag is set.
    /// </summary>
    /// <param name="flag">The flag</param>
    /// <param name="mark">The mark</param>
    /// <returns>The mark, or an empty text</returns>
    private static string Marks(bool flag, string mark)
    {
        return flag ? mark : string.Empty;
    }

    #endregion // Methods
}