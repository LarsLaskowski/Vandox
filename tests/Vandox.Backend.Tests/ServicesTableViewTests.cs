using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ServicesTableView"/> and the texts of <see cref="ServicesView"/> that it uses
/// </summary>
[TestClass]
public class ServicesTableViewTests
{
    #region Fields

    private static readonly DateTimeOffset _now = ServicesSamples.Now;

    private static readonly HistoryRange _month = new(_now.AddDays(-30), _now);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The columns are the seven of the table and a row writes the unit, the shares and times, the restarts, the last restart or <c>none</c> and the latest state with its time; a unit
    /// that was failed for 2 hours of a month recorded throughout reads 99.7 %, 2 h 0 min and 0 s, a unit without time not available reads 100.0 %.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC3")]
    public void ServicesTableViewWritesColumnsAndLatestState()
    {
        // Arrange
        var availability = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromHours(718), TimeSpan.FromHours(2), TimeSpan.Zero, "failed", "failed", _now.AddHours(-2)),
                                                        ServicesSamples.Unit("sshd.service", TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero, "active", "running", _now.AddDays(-3)));

        // Act
        var table = ServicesTableView.Build(availability, new SeriesMarkerList(), _month);

        // Assert
        var nginx = ServicesSamples.CellTexts(table.Units.Rows.Single(row => HistorySamples.Text(row.Cells[0]) == ServicesSamples.Nginx));
        var sshd = ServicesSamples.CellTexts(table.Units.Rows.Single(row => HistorySamples.Text(row.Cells[0]) == "sshd.service"));

        Assert.AreEqual(ServicesView.ServicesHeading, table.Units.Heading, "the caption of the table");
        Assert.AreSequenceEqual(["Unit", "Available", "Not available", "Unknown", "Restarts", "Last restart", "Latest state"], table.Units.Columns.ToArray(), "the columns");
        Assert.AreSequenceEqual([ServicesSamples.Nginx, "99.7 %", "2 h 0 min", "0 s", "0", "none"], nginx.Take(6).ToArray(), "the cells before the latest state");
        Assert.AreEqual("failed (failed), 2026-10-01 08:00:00 UTC", nginx[6], "the latest state with its sub state and its time");
        Assert.AreSequenceEqual(["sshd.service", "100.0 %", "0 s", "0 s", "0", "none"], sshd.Take(6).ToArray(), "no time not available reads 100.0 %");
        Assert.StartsWith("active (running), ", sshd[6], "the latest state of the other unit");
        Assert.IsFalse(table.Empty, "units are listed");
    }

    /// <summary>
    /// A unit without any known time reads <c>unknown</c> in the column <c>Available</c>, and its unknown time is the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServicesTableViewWritesUnknownWithoutKnownTime()
    {
        // Arrange
        var availability = ServicesSamples.Availability(ServicesSamples.Unit("gone.service", TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(30), "inactive", "dead", _now.AddDays(-40)));

        // Act
        var table = ServicesTableView.Build(availability, new SeriesMarkerList(), _month);

        // Assert
        var cells = ServicesSamples.CellTexts(table.Units.Rows.Single());

        Assert.AreSequenceEqual(["gone.service", "unknown", "0 s", "30 d 0 h"], cells.Take(4).ToArray(), "no known time: the share is unknown, never a number");
    }

    /// <summary>
    /// Rows with restarts or time not available come first, by restarts and then time not available descending, then every other unit in ordinal order of the names, which puts
    /// upper case before lower case.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void ServicesTableViewOrdersByRestartsThenNotAvailableThenName()
    {
        // Arrange
        var hour = TimeSpan.FromHours(1);
        var availability = ServicesSamples.Availability(ServicesSamples.Unit("a.service", TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero),
                                                        ServicesSamples.Unit("f.service", TimeSpan.FromDays(30), TimeSpan.FromMinutes(5), TimeSpan.Zero),
                                                        ServicesSamples.Unit("c.service", TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero),
                                                        ServicesSamples.Unit("B.service", TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero),
                                                        ServicesSamples.Unit("g.service", TimeSpan.FromDays(30), hour, TimeSpan.Zero),
                                                        ServicesSamples.Unit("d.service", TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero),
                                                        ServicesSamples.Unit("e.service", TimeSpan.FromDays(30), hour, TimeSpan.Zero));
        var restarts = new SeriesMarkerList
                       {
                           Markers = [
                                         ServicesSamples.Restart(_now.AddDays(-1), "c.service"),
                                         ServicesSamples.Restart(_now.AddDays(-2), "d.service"),
                                         ServicesSamples.Restart(_now.AddDays(-3), "d.service"),
                                         ServicesSamples.Restart(_now.AddDays(-4), "d.service"),
                                         ServicesSamples.Restart(_now.AddDays(-5), "e.service"),
                                         ServicesSamples.Restart(_now.AddDays(-6), "e.service"),
                                         ServicesSamples.Restart(_now.AddDays(-7), "e.service")
                                     ]
                       };

        // Act
        var table = ServicesTableView.Build(availability, restarts, _month);

        // Assert
        var units = table.Units.Rows.Select(row => HistorySamples.Text(row.Cells[0])).ToArray();

        Assert.AreSequenceEqual(["e.service", "d.service", "c.service", "g.service", "f.service", "B.service", "a.service"], units, "restarts descending, then time not available descending, then the rest in ordinal order");
    }

    /// <summary>
    /// A unit that restarted three times reads 3 with the time of the latest restart, and a unit without a restart reads 0 and <c>none</c>.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ServicesTableViewCountsRestartsPerUnit()
    {
        // Arrange
        var availability = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.PhpFpm, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero), ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));
        var restarts = new SeriesMarkerList
                       {
                           Markers = [
                                         ServicesSamples.Restart(new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero), ServicesSamples.PhpFpm),
                                         ServicesSamples.Restart(new DateTimeOffset(2026, 9, 30, 10, 0, 5, TimeSpan.Zero), ServicesSamples.PhpFpm),
                                         ServicesSamples.Restart(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero), ServicesSamples.PhpFpm)
                                     ]
                       };

        // Act
        var table = ServicesTableView.Build(availability, restarts, _month);

        // Assert
        var php = ServicesSamples.CellTexts(table.Units.Rows[0]);
        var nginx = ServicesSamples.CellTexts(table.Units.Rows[1]);

        Assert.AreEqual(ServicesSamples.PhpFpm, php[0], "the unit with restarts is first");
        Assert.AreSequenceEqual(["3", "2026-09-30 10:00:05"], php.Skip(4).Take(2).ToArray(), "three restarts and the time of the latest");
        Assert.AreSequenceEqual(["0", "none"], nginx.Skip(4).Take(2).ToArray(), "no restart");
    }

    /// <summary>
    /// The list of the restarts holds the time and the unit of each, in time order, whatever the order of the markers.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ServicesTableViewListsRestartsInTimeOrder()
    {
        // Arrange
        var availability = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.PhpFpm, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));
        var restarts = new SeriesMarkerList
                       {
                           Markers = [
                                         ServicesSamples.Restart(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero), ServicesSamples.PhpFpm),
                                         ServicesSamples.Restart(new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero), ServicesSamples.Nginx),
                                         ServicesSamples.Restart(new DateTimeOffset(2026, 9, 30, 10, 0, 5, TimeSpan.Zero), ServicesSamples.PhpFpm)
                                     ]
                       };

        // Act
        var table = ServicesTableView.Build(availability, restarts, _month);

        // Assert
        Assert.AreEqual(ServicesView.RestartsHeading, table.Restarts.Heading, "the caption of the list");
        Assert.AreSequenceEqual(["Time", "Unit"], table.Restarts.Columns.ToArray(), "the columns of the list");
        Assert.AreSequenceEqual(["2026-09-21 10:00:00|nginx.service", "2026-09-26 10:00:00|php-fpm.service", "2026-09-30 10:00:05|php-fpm.service"], table.Restarts.Rows.Select(ServicesSamples.Cells).ToArray(), "time and unit in time order");
    }

    /// <summary>
    /// The list shows the 1,000 restarts the store reads at most, and no restart list is written without a restart.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ServicesTableViewListsAThousandRestarts()
    {
        // Arrange
        var availability = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.PhpFpm, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));
        var restarts = new SeriesMarkerList
                       {
                           Markers = [.. Enumerable.Range(0, TimeSeriesService.MaxMarkers).Select(index => ServicesSamples.Restart(_month.Start.AddMinutes(index), ServicesSamples.PhpFpm))]
                       };

        // Act
        var table = ServicesTableView.Build(availability, restarts, _month);
        var none = ServicesTableView.Build(availability, new SeriesMarkerList(), _month);

        // Assert
        Assert.HasCount(1000, table.Restarts.Rows, "every restart is listed");
        Assert.AreEqual("1000", ServicesSamples.CellTexts(table.Units.Rows[0])[4], "and counted");
        Assert.IsEmpty(none.Restarts.Rows, "no restart, no row");
    }

    /// <summary>
    /// A restart read that reached its cap or could not decide a restart ends the notes with the time after which restarts are not counted; one that is complete says nothing.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ServicesTableViewSaysRestartsNotCounted()
    {
        // Arrange
        var availability = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.PhpFpm, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));
        var cut = new SeriesMarkerList
                  {
                      Markers = [ServicesSamples.Restart(_now.AddDays(-10), ServicesSamples.PhpFpm)],
                      CompleteUntil = new DateTimeOffset(2026, 9, 25, 8, 30, 15, TimeSpan.Zero)
                  };

        // Act
        var incomplete = ServicesTableView.Build(availability, cut, _month);
        var complete = ServicesTableView.Build(availability, new SeriesMarkerList(), _month);

        // Assert
        Assert.AreSequenceEqual(["Restarts after 2026-09-25 08:30:15 UTC are not counted."], incomplete.Notes.ToArray(), "the note of the cut restarts");
        Assert.IsEmpty(complete.Notes, "a complete read has no note");
    }

    /// <summary>
    /// A read that left units out says how many more units are not evaluated, below the table; a read that evaluated every unit says nothing.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void ServicesTableViewSaysMoreUnitsNotEvaluated()
    {
        // Arrange
        var units = Enumerable.Range(0, 256).Select(index => ServicesSamples.Unit($"u{index:000}.service", TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero)).ToArray();
        var cut = new ServiceAvailabilityResult
                  {
                      Units = units,
                      UnitCount = 300
                  };

        // Act
        var table = ServicesTableView.Build(cut, new SeriesMarkerList(), _month);
        var whole = ServicesTableView.Build(ServicesSamples.Availability(units), new SeriesMarkerList(), _month);

        // Assert
        Assert.HasCount(256, table.Units.Rows, "256 units are listed");
        Assert.AreEqual("44 more units are not evaluated.", table.Units.More, "the number of units that are not evaluated");
        Assert.AreEqual(string.Empty, whole.Units.More, "every unit is evaluated");
    }

    /// <summary>
    /// Without a unit the table is empty, and the page shows the text of the missing service state in its place.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void ServicesTableViewShowsNoServiceState()
    {
        // Arrange and act
        var table = ServicesTableView.Build(new ServiceAvailabilityResult(), new SeriesMarkerList(), _month);

        // Assert
        Assert.IsTrue(table.Empty, "no unit, no table");
        Assert.IsEmpty(table.Units.Rows, "no row");
        Assert.AreEqual("No service state is stored in this range.", ServicesView.NoServiceStateText, "the text for the page");
    }

    /// <summary>
    /// A unit name is external text: it is shown as the display of its text, with control and direction characters as visible escapes, markup as text and at most 256 characters, in the
    /// table of the units and in the list of the restarts.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void ServicesTableViewEscapesUnitNames()
    {
        // Arrange
        var evil = $"evil{ServicesSamples.Rlo}<script>alert(1)</script>{ServicesSamples.Zwsp}{new string('x', 300)}.service";
        var availability = ServicesSamples.Availability(ServicesSamples.Unit(evil, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));
        var restarts = new SeriesMarkerList
                       {
                           Markers = [ServicesSamples.Restart(_now.AddDays(-1), evil)]
                       };

        // Act
        var table = ServicesTableView.Build(availability, restarts, _month);

        // Assert
        var expected = ServicesSamples.Describe(SnapshotCells.Runs(evil));
        var inTable = table.Units.Rows[0].Cells[0];
        var inList = table.Restarts.Rows[0].Cells[1];

        Assert.Contains(run => run.Escaped, inTable, "the name needs escapes");
        Assert.AreEqual(expected, ServicesSamples.Describe(inTable), "the unit in the table is the display of its text");
        Assert.AreEqual(expected, ServicesSamples.Describe(inList), "the unit in the list of the restarts is the display of its text");
        Assert.DoesNotContain(ServicesSamples.Rlo, string.Concat(inTable.Where(run => run.Escaped == false).Select(run => run.Text)), "the direction character is only escaped");
        Assert.IsLessThanOrEqualTo(HistoryView.TextChars + 64, HistorySamples.Text(inTable).Length, "the name is capped");
    }

    /// <summary>
    /// When a gap of the systemd collector overlaps the range the notes begin with the text that says so, and not otherwise.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServicesTableViewShowsSystemdGapNote()
    {
        // Arrange
        var units = new[] { ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromDays(29), TimeSpan.Zero, TimeSpan.FromDays(1)) };
        var withGaps = new ServiceAvailabilityResult
                       {
                           Units = units,
                           UnitCount = 1,
                           SystemdGaps = true
                       };

        // Act
        var gaps = ServicesTableView.Build(withGaps, new SeriesMarkerList(), _month);
        var none = ServicesTableView.Build(ServicesSamples.Availability(units), new SeriesMarkerList(), _month);

        // Assert
        Assert.AreSequenceEqual(["The systemd collector recorded gaps in this range (it failed, or it refused a unit's state); that time counts as unknown for every unit."], gaps.Notes.ToArray(), "the note of the gaps");
        Assert.IsEmpty(none.Notes, "no gap, no note");
    }

    /// <summary>
    /// A gap read that stopped at its cap says that gaps after that time are not read, and the notes keep the order: the gaps of the systemd collector, the gaps not read, the restarts not counted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ServicesTableViewShowsGapCapNote()
    {
        // Arrange
        var units = new[] { ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromDays(10), TimeSpan.Zero, TimeSpan.FromDays(20)) };
        var capped = new ServiceAvailabilityResult
                     {
                         Units = units,
                         UnitCount = 1,
                         SystemdGaps = true,
                         GapsCompleteUntil = new DateTimeOffset(2026, 9, 10, 4, 5, 6, TimeSpan.Zero)
                     };
        var restarts = new SeriesMarkerList
                       {
                           CompleteUntil = new DateTimeOffset(2026, 9, 25, 8, 30, 15, TimeSpan.Zero)
                       };

        // Act
        var table = ServicesTableView.Build(capped, restarts, _month);

        // Assert
        Assert.AreSequenceEqual([
                                    ServicesView.SystemdGapsText,
                                    "Gaps after 2026-09-10 04:05:06 UTC are not read; the time after it counts as unknown.",
                                    "Restarts after 2026-09-25 08:30:15 UTC are not counted."
                                ],
                                table.Notes.ToArray(),
                                "the notes in the order of the edge cases");
    }

    /// <summary>
    /// A share is the part of the total rounded down to one decimal, so that 100.0 % is shown only when nothing is missing and a share never rounds up to the next tenth.
    /// </summary>
    /// <param name="partSeconds">The part in seconds</param>
    /// <param name="totalSeconds">The total in seconds</param>
    /// <param name="expected">The text</param>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    [DataRow(0, 100, "0.0 %")]
    [DataRow(100, 100, "100.0 %")]
    [DataRow(2584800, 2592000, "99.7 %")]
    [DataRow(86399, 86400, "99.9 %")]
    [DataRow(99999, 100000, "99.9 %")]
    [DataRow(85800, 86400, "99.3 %")]
    [DataRow(600, 86400, "0.6 %")]
    [DataRow(1, 86400, "0.0 %")]
    [DataRow(1, 3, "33.3 %")]
    [DataRow(2, 3, "66.6 %")]
    [DataRow(7, 8, "87.5 %")]
    public void ServicesViewShareDownNeverShowsHundredWithTimeNotAvailable(int partSeconds, int totalSeconds, string expected)
    {
        // Arrange
        var part = TimeSpan.FromSeconds(partSeconds);
        var total = TimeSpan.FromSeconds(totalSeconds);

        // Act
        var text = ServicesView.ShareDown(part, total);

        // Assert
        Assert.AreEqual(expected, text, "the share rounded down to one decimal");
    }

    /// <summary>
    /// A unit without a record time reads <c>unknown</c> as its latest state, and a share of a total of zero reads 0.0 %.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void ServicesTableViewWritesUnknownLatestStateAndZeroShare()
    {
        // Arrange
        var availability = ServicesSamples.Availability(ServicesSamples.Unit("x.service", TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(30), string.Empty, string.Empty, null));

        // Act
        var table = ServicesTableView.Build(availability, new SeriesMarkerList(), _month);

        // Assert
        Assert.AreEqual("unknown", ServicesSamples.CellTexts(table.Units.Rows[0])[6], "no latest record");
        Assert.AreEqual("0.0 %", ServicesView.ShareDown(TimeSpan.Zero, TimeSpan.Zero), "a total of zero");
    }

    #endregion // Methods
}