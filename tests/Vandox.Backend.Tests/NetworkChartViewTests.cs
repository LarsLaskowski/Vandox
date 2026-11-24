using Vandox.Backend.History;
using Vandox.Backend.Network;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="NetworkChartView"/>
/// </summary>
[TestClass]
public class NetworkChartViewTests
{
    #region Constants

    private const string NoDataNote = "No connection data is stored in this range.";
    private const string CutListNote = "The list shows the first 256 sources.";
    private const string StartsBeforeNote = "This range starts before the oldest stored connection data of this source.";
    private const string IncompleteNote = "Some snapshots in this range may not cover every socket.";
    private const string NotFoldedNote = "Values after 2026-10-01 10:20:00 UTC are not complete yet and may still change.";
    private const string StepNote = "Each point is the average of 10 minutes; the legend shows the minimum and maximum.";
    private const string AddressNote = "Each snapshot counts only its 25 most frequent remote addresses and the rollups keep only the most frequent ones; an address outside them counts 0 there.";
    private const string CapNote = "Some periods in this range held more remote addresses than the rollups keep, so an address can read lower there and the count of further addresses is a lower bound.";
    private const char RightToLeftOverride = (char)0x202E;

    #endregion // Constants

    #region Fields

    private static readonly HistoryRange _range = new(HistorySamples.Base, HistorySamples.Base.AddHours(1));

    private static readonly TimeSpan _step = TimeSpan.FromMinutes(10);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The state chart has one band per state line in the order of the result, with the average of each step as its value, the axis Sockets and the stacked mode.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void NetworkChartViewStateChartStacksEveryStateInNameOrder()
    {
        // Arrange
        var result = NetworkSamples.Rollups(HistorySamples.Base,
                                            [
                                                NetworkSamples.Line("ESTABLISHED", 10, 12),
                                                NetworkSamples.Line("LISTEN", 3, 3),
                                                NetworkSamples.Line("TIME_WAIT", 5, null),
                                                NetworkSamples.Line("UDP", 2, 1)
                                            ],
                                            []);

        // Act
        var view = NetworkChartView.StateChart(result);

        // Assert
        Assert.AreSequenceEqual(["ESTABLISHED", "LISTEN", "TIME_WAIT", "UDP"], view.Lines.Select(line => line.Label).ToArray(), "one band per state in the order of the result");
        Assert.AreSequenceEqual([10.0, 12.0], view.Lines[0].Values.ToArray(), "the averages of the first state");
        Assert.AreSequenceEqual([2.0, 1.0], view.Lines[3].Values.ToArray(), "the averages of UDP");
        Assert.AreSequenceEqual(["Sockets"], view.Axes.ToArray(), "the axis");
        Assert.IsTrue(view.Stacked, "the bands are stacked");
        Assert.HasCount(result.Times.Count, view.Times, "the steps of the result");
        Assert.AreSequenceEqual([0, 1, 2, 3], view.Lines.Select(line => line.Color).ToArray(), "a color per band");
        Assert.IsTrue(view.Lines.All(line => line.Scale == 0 && line.Dashed == false), "one scale and no dashes up to six bands");
        Assert.IsTrue(view.Lines.All(line => line.Bridged.All(bridged => bridged == false)), "no band is bridged");
        Assert.IsNull(view.Moment, "no moment is marked");
        Assert.IsEmpty(view.Marks, "no marks");
        Assert.IsEmpty(view.Notes, "no notes of its own");
    }

    /// <summary>
    /// Bands beyond the sixth reuse the colors and are dashed, as the processes view's lines do.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void NetworkChartViewLinesReuseColorsAndDashFromTheSeventh()
    {
        // Arrange
        var states = Enumerable.Range(0, 8).Select(index => NetworkSamples.Line($"S{index}", 1, 1)).ToArray();

        // Act
        var view = NetworkChartView.StateChart(NetworkSamples.Rollups(HistorySamples.Base, states, []));

        // Assert
        Assert.AreSequenceEqual([0, 1, 2, 3, 4, 5, 0, 1], view.Lines.Select(line => line.Color).ToArray(), "six colors in turn");
        Assert.AreSequenceEqual([false, false, false, false, false, false, true, true], view.Lines.Select(line => line.Dashed).ToArray(), "dashed from the seventh");
    }

    /// <summary>
    /// A band carries the minimum and the maximum of each step for the legend, null where the step has no point.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void NetworkChartViewLinesCarryMinimumAndMaximum()
    {
        // Arrange
        var state = new ConnectionRollupLine
                    {
                        Name = "ESTABLISHED",
                        Points = [NetworkSamples.Point(4), null, new ProgramRollupPoint(6, 2, 9)]
                    };
        var result = new ConnectionRollupResult
                     {
                         Times = ProcessesSamples.Times(HistorySamples.Base, _step, 3),
                         States = [state],
                         Others = [1, 1, 1],
                         Incomplete = [0, 0, 0]
                     };

        // Act
        var view = NetworkChartView.StateChart(result);

        // Assert
        Assert.AreSequenceEqual([4.0, null, 6.0], view.Lines[0].Values.ToArray(), "the averages");
        Assert.AreSequenceEqual([2.0, null, 2.0], view.Lines[0].Minimums.ToArray(), "the minimums");
        Assert.AreSequenceEqual([8.0, null, 9.0], view.Lines[0].Maximums.ToArray(), "the maximums");
    }

    /// <summary>
    /// The program chart stacks the programs of the result in rank order and the other programs on top, with the axis Sockets.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void NetworkChartViewProgramChartStacksTopProgramsAndOthers()
    {
        // Arrange
        var result = NetworkSamples.Rollups(HistorySamples.Base,
                                            [NetworkSamples.Line("ESTABLISHED", 20, 20)],
                                            [NetworkSamples.Line("nginx", 8, 9), NetworkSamples.Line("mysqld", 4, 3)],
                                            [2, 3]);

        // Act
        var view = NetworkChartView.ProgramChart(result);

        // Assert
        Assert.AreSequenceEqual(["nginx", "mysqld", "Other programs"], view.Lines.Select(line => line.Label).ToArray(), "the programs in rank order, the other programs on top");
        Assert.AreSequenceEqual([8.0, 9.0], view.Lines[0].Values.ToArray(), "the first program");
        Assert.AreSequenceEqual([2.0, 3.0], view.Lines[2].Values.ToArray(), "the other programs");
        Assert.AreSequenceEqual([4.0, 4.5], view.Lines[0].Minimums.Select(value => value ?? 0).Take(2).ToArray(), "a program carries its minimum");
        Assert.IsEmpty(view.Lines[2].Minimums, "the other programs have no minimum");
        Assert.IsEmpty(view.Lines[2].Maximums, "the other programs have no maximum");
        Assert.AreSequenceEqual(["Sockets"], view.Axes.ToArray(), "the axis");
        Assert.IsTrue(view.Stacked, "the bands are stacked");
        Assert.AreSequenceEqual([0, 1, 2], view.Lines.Select(line => line.Color).ToArray(), "a color per band");
    }

    /// <summary>
    /// A result without programs still has the other programs as its only band.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void NetworkChartViewProgramChartWithoutProgramsHasTheOtherProgramsOnly()
    {
        // Arrange
        var result = NetworkSamples.Rollups(HistorySamples.Base, [NetworkSamples.Line("UDP", 1, 1)], [], [5, 6]);

        // Act
        var view = NetworkChartView.ProgramChart(result);

        // Assert
        Assert.AreSequenceEqual(["Other programs"], view.Lines.Select(line => line.Label).ToArray(), "one band");
        Assert.AreSequenceEqual([5.0, 6.0], view.Lines[0].Values.ToArray(), "its values");
    }

    /// <summary>
    /// A step whose snapshot was not folded (a null in the other programs) is null in every band of both charts and no band is bridged across it.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void NetworkChartViewGapBreaksEveryBand()
    {
        // Arrange
        var result = NetworkSamples.Rollups(HistorySamples.Base,
                                            [NetworkSamples.Line("ESTABLISHED", 3, 3, 3), NetworkSamples.Line("UDP", 1, 1, 1)],
                                            [NetworkSamples.Line("nginx", 2, 2, 2)],
                                            [1, null, 1]);

        // Act
        var states = NetworkChartView.StateChart(result);
        var programs = NetworkChartView.ProgramChart(result);

        // Assert
        foreach (var line in states.Lines.Concat(programs.Lines))
        {
            Assert.IsNull(line.Values[1], $"{line.Label}: the gap step is null");
            Assert.IsNotNull(line.Values[0], $"{line.Label}: the step before the gap keeps its value");
            Assert.IsNotNull(line.Values[2], $"{line.Label}: the step after the gap keeps its value");
            Assert.IsTrue(line.Bridged.All(bridged => bridged == false), $"{line.Label}: nothing is bridged");
        }
    }

    /// <summary>
    /// A hostile state or program name is shown escaped and cut at 64 characters in the label of its band.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void NetworkChartViewEscapesAndCapsLabels()
    {
        // Arrange
        var result = NetworkSamples.Rollups(HistorySamples.Base,
                                            [NetworkSamples.Line($"evil{RightToLeftOverride}state", 1, 1)],
                                            [NetworkSamples.Line($"evil{RightToLeftOverride}name{(char)7}", 1, 1), NetworkSamples.Line(new string('a', 100), 1, 1)]);

        // Act
        var states = NetworkChartView.StateChart(result);
        var programs = NetworkChartView.ProgramChart(result);

        // Assert
        Assert.AreEqual("evil\\u202estate", states.Lines[0].Label, "the control character of a state is written as an escape");
        Assert.AreEqual("evil\\u202ename\\u0007", programs.Lines[0].Label, "the control characters of a program are written as escapes");
        Assert.AreEqual(new string('a', 64) + "[cut: 36 more characters]", programs.Lines[1].Label, "the label is capped at 64 characters");
    }

    /// <summary>
    /// The builders refuse no result.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void NetworkChartViewRefusesNullArguments()
    {
        // Act and assert
        Assert.Throws<ArgumentNullException>(() => NetworkChartView.StateChart(null!), "no result for the states");
        Assert.Throws<ArgumentNullException>(() => NetworkChartView.ProgramChart(null!), "no result for the programs");
        Assert.Throws<ArgumentNullException>(() => NetworkChartView.AddressTable(null!), "no result for the table");
        Assert.Throws<ArgumentNullException>(() => NetworkChartView.AddressTableNotes(null!), "no result for the notes");
        Assert.Throws<ArgumentNullException>(() => NetworkChartView.ChartNotes(null!, new ProgramSourceList(), _range, _step), "no rollups");
        Assert.Throws<ArgumentNullException>(() => NetworkChartView.ChartNotes(new ConnectionRollupResult(), null!, _range, _step), "no sources");
        Assert.Throws<ArgumentNullException>(() => NetworkChartView.ChartNotes(new ConnectionRollupResult(), new ProgramSourceList(), null!, _step), "no range");
    }

    /// <summary>
    /// The address table has the three columns, one row per address in the order of the result with the average to one decimal and the highest count, and ends without a further line when the rollups hold no more.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void NetworkChartViewAddressTableListsTopTwentyWithFurther()
    {
        // Arrange
        var few = NetworkSamples.Addresses(HistorySamples.Base, NetworkSamples.Address("203.0.113.7", 12.34, 40), NetworkSamples.Address("198.51.100.2", 3, 5));

        // Act
        var table = NetworkChartView.AddressTable(few);

        // Assert
        Assert.AreSequenceEqual(["Address", "Average connections", "Highest in one snapshot"], table.Columns.ToArray(), "the columns");
        Assert.AreSequenceEqual(["203.0.113.7|12.3|40", "198.51.100.2|3.0|5"], table.Rows.Select(NetworkSamples.Cells).ToArray(), "the rows with the average to one decimal");
        Assert.AreEqual(string.Empty, table.More, "no further addresses");
    }

    /// <summary>
    /// Addresses are ordered by average descending and then by address, cut at 20 rows, and the rest of the list plus the further addresses of the rollups are counted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void NetworkChartViewAddressTableCutsAtTwentyAndCountsTheRest()
    {
        // Arrange
        var addresses = Enumerable.Range(1, 23).Select(index => NetworkSamples.Address($"203.0.113.{index}", 100 - index, index)).Reverse().ToArray();
        var tie = NetworkSamples.Addresses(HistorySamples.Base, NetworkSamples.Address("b.example", 5, 5), NetworkSamples.Address("a.example", 5, 6), NetworkSamples.Address("c.example", 9, 1));
        var result = new ConnectionAddressResult
                     {
                         Times = ProcessesSamples.Times(HistorySamples.Base, _step, 2),
                         Gaps = [false, false],
                         Addresses = addresses,
                         Further = 5,
                         Incomplete = [0, 0],
                         Capped = [0, 0]
                     };

        // Act
        var table = NetworkChartView.AddressTable(result);
        var tied = NetworkChartView.AddressTable(tie);

        // Assert
        Assert.HasCount(NetworkView.AddressRows, table.Rows, "20 rows");
        Assert.AreEqual("203.0.113.1|99.0|1", NetworkSamples.Cells(table.Rows[0]), "the highest average first");
        Assert.AreEqual("203.0.113.20|80.0|20", NetworkSamples.Cells(table.Rows[19]), "the twentieth");
        Assert.AreEqual("and 8 more addresses", table.More, "three beyond the cut and five further addresses of the rollups");
        Assert.AreSequenceEqual(["c.example", "a.example", "b.example"], tied.Rows.Select(row => HistorySamples.Text(row.Cells[0])).ToArray(), "by average descending, then by address");
    }

    /// <summary>
    /// With exactly 20 addresses and none further there is no further line; with further addresses of the rollups alone there is.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void NetworkChartViewAddressTableSaysFurtherOnlyWhenThereAreMore()
    {
        // Arrange
        var twenty = NetworkSamples.Addresses(HistorySamples.Base, [.. Enumerable.Range(1, 20).Select(index => NetworkSamples.Address($"203.0.113.{index}", index, index))]);
        var further = new ConnectionAddressResult
                      {
                          Addresses = twenty.Addresses,
                          Further = 7
                      };

        // Act
        var exact = NetworkChartView.AddressTable(twenty);
        var more = NetworkChartView.AddressTable(further);
        var empty = NetworkChartView.AddressTable(new ConnectionAddressResult());

        // Assert
        Assert.AreEqual(string.Empty, exact.More, "exactly 20 addresses");
        Assert.AreEqual("and 7 more addresses", more.More, "further addresses of the rollups");
        Assert.IsEmpty(empty.Rows, "no rows for an empty result");
        Assert.AreEqual(string.Empty, empty.More, "no further line for an empty result");
    }

    /// <summary>
    /// A hostile remote address is external text: its cell is built from escaped runs.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void NetworkChartViewAddressTableEscapesTheAddress()
    {
        // Arrange
        var result = NetworkSamples.Addresses(HistorySamples.Base, NetworkSamples.Address($"203.0.113.7{RightToLeftOverride}", 1, 1));

        // Act
        var table = NetworkChartView.AddressTable(result);

        // Assert
        Assert.AreEqual("203.0.113.7\\u202e", HistorySamples.Text(table.Rows[0].Cells[0]), "the address is written with its escape");
        Assert.IsTrue(NetworkSamples.AnyEscaped(table.Rows[0].Cells[0]), "a run is marked escaped");
    }

    /// <summary>
    /// The note of the counts always follows the table, and the note of capped periods is added exactly when a step lost an address to a bucket's cap.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void NetworkChartViewAddressTableNotesNameCapped()
    {
        // Arrange
        var plain = NetworkSamples.Addresses(HistorySamples.Base, NetworkSamples.Address("203.0.113.7", 1, 1));
        var capped = new ConnectionAddressResult
                     {
                         Times = plain.Times,
                         Gaps = plain.Gaps,
                         Addresses = plain.Addresses,
                         Incomplete = [0, 0],
                         Capped = [0, 3]
                     };

        // Act
        var without = NetworkChartView.AddressTableNotes(plain);
        var with = NetworkChartView.AddressTableNotes(capped);
        var empty = NetworkChartView.AddressTableNotes(new ConnectionAddressResult());

        // Assert
        Assert.AreSequenceEqual([AddressNote], without.ToArray(), "the count note alone");
        Assert.AreSequenceEqual([AddressNote, CapNote], with.ToArray(), "the count note, then the note of the cap");
        Assert.AreSequenceEqual([AddressNote], empty.ToArray(), "the count note for an empty result as well");
    }

    /// <summary>
    /// A result without steps gives only the note that no data is stored.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void NetworkChartViewChartNotesAddsTheNoDataNoteOnlyForAnEmptyRange()
    {
        // Arrange
        var empty = new ConnectionRollupResult();
        var data = NetworkSamples.Rollups(HistorySamples.Base, [NetworkSamples.Line("UDP", 1, 1)], []);

        // Act
        var none = NetworkChartView.ChartNotes(empty, new ProgramSourceList(), _range, _step);
        var some = NetworkChartView.ChartNotes(data, new ProgramSourceList(), _range, _step);

        // Assert
        Assert.AreSequenceEqual([NoDataNote], none.ToArray(), "an empty result gives only the note");
        Assert.AreSequenceEqual([StepNote], some.ToArray(), "a result with steps has the step note only");
    }

    /// <summary>
    /// The notes of the oldest data, incomplete snapshots and values not folded appear exactly when the read calls for them, the last one only before the end of the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void NetworkChartViewChartNotesAppearExactlyWhenTheReadCallsForThem()
    {
        // Arrange
        var line = new[] { NetworkSamples.Line("UDP", 1, 1) };
        var before = Result(line, startsBefore: true);
        var incomplete = Result(line, incomplete: [0, 2]);
        var unfolded = Result(line, unfolded: HistorySamples.Base.AddMinutes(20));
        var atEnd = Result(line, unfolded: _range.End);
        var after = Result(line, unfolded: _range.End.AddMinutes(1));
        var clean = Result(line);

        // Act and assert
        Assert.AreSequenceEqual([StartsBeforeNote, StepNote], Notes(before), "the oldest data");
        Assert.AreSequenceEqual([IncompleteNote, StepNote], Notes(incomplete), "an incomplete snapshot");
        Assert.AreSequenceEqual([NotFoldedNote, StepNote], Notes(unfolded), "values not folded yet");
        Assert.AreSequenceEqual([StepNote], Notes(atEnd), "unfolded at the end of the range");
        Assert.AreSequenceEqual([StepNote], Notes(after), "unfolded after the end of the range");
        Assert.AreSequenceEqual([StepNote], Notes(clean), "nothing to note");
    }

    /// <summary>
    /// A cut source list adds its note, and the notes come in the order list, start, incomplete, not folded, step.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void NetworkChartViewChartNotesKeepTheOrder()
    {
        // Arrange
        var everything = Result([NetworkSamples.Line("UDP", 1, 1)], startsBefore: true, incomplete: [1, 0], unfolded: HistorySamples.Base.AddMinutes(20));

        // Act
        var all = NetworkChartView.ChartNotes(everything,
                                              new ProgramSourceList
                                              {
                                                  More = true
                                              },
                                              _range,
                                              _step);
        var cutOnly = NetworkChartView.ChartNotes(Result([NetworkSamples.Line("UDP", 1, 1)]),
                                                  new ProgramSourceList
                                                  {
                                                      More = true
                                                  },
                                                  _range,
                                                  _step);

        // Assert
        Assert.AreSequenceEqual([CutListNote, StartsBeforeNote, IncompleteNote, NotFoldedNote, StepNote], all.ToArray(), "every note in order");
        Assert.AreSequenceEqual([CutListNote, StepNote], cutOnly.ToArray(), "the cut list and the step note");
    }

    /// <summary>
    /// The step note names the step in minutes, hours or days.
    /// </summary>
    /// <param name="minutes">The step in minutes</param>
    /// <param name="text">The step as the note says it</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(5, "5 minutes")]
    [DataRow(60, "1 hour")]
    [DataRow(720, "12 hours")]
    public void NetworkChartViewChartNotesWriteTheStepNote(int minutes, string text)
    {
        // Arrange
        var result = Result([NetworkSamples.Line("UDP", 1, 1)]);

        // Act
        var notes = NetworkChartView.ChartNotes(result, new ProgramSourceList(), _range, TimeSpan.FromMinutes(minutes));

        // Assert
        Assert.AreSequenceEqual([$"Each point is the average of {text}; the legend shows the minimum and maximum."], notes.ToArray(), "the step note");
    }

    /// <summary>
    /// Steps past the other-programs list are gaps: every line is null there.
    /// </summary>
    [TestMethod]
    public void NetworkChartViewStepsPastOthersAreGaps()
    {
        // Arrange
        var result = new ConnectionRollupResult
                     {
                         Times = ProcessesSamples.Times(HistorySamples.Base, _step, 3),
                         Programs = [
                                        new ConnectionRollupLine
                                        {
                                            Name = "nginx",
                                            Points = [NetworkSamples.Point(4), NetworkSamples.Point(5), NetworkSamples.Point(6)]
                                        }
                                    ],
                         Others = [1, 1],
                         Incomplete = [0, 0, 0]
                     };

        // Act
        var view = NetworkChartView.ProgramChart(result);

        // Assert
        Assert.AreSequenceEqual([4.0, 5.0, null], view.Lines[0].Values.ToArray(), "the program line has no value past the others");
        Assert.AreSequenceEqual([1.0, 1.0, null], view.Lines[1].Values.ToArray(), "the other programs have no value past their list");
    }

    /// <summary>
    /// Gives the notes of a result with the range, the step and a source list that is not cut.
    /// </summary>
    /// <param name="result">The result</param>
    /// <returns>The notes</returns>
    private static string[] Notes(ConnectionRollupResult result)
    {
        return [.. NetworkChartView.ChartNotes(result, new ProgramSourceList(), _range, _step)];
    }

    /// <summary>
    /// Creates a rollup result of two steps with the flags the notes depend on.
    /// </summary>
    /// <param name="states">The state lines</param>
    /// <param name="startsBefore">Whether the range starts before the oldest buckets</param>
    /// <param name="incomplete">The incomplete snapshots per step, or <c>null</c> for none</param>
    /// <param name="unfolded">The oldest unfolded snapshot, or <c>null</c></param>
    /// <returns>The result</returns>
    private static ConnectionRollupResult Result(ConnectionRollupLine[] states, bool startsBefore = false, int[]? incomplete = null, DateTimeOffset? unfolded = null)
    {
        return new ConnectionRollupResult
               {
                   Times = ProcessesSamples.Times(HistorySamples.Base, _step, 2),
                   States = states,
                   Others = [1, 1],
                   Incomplete = incomplete ?? [0, 0],
                   StartsBeforeBuckets = startsBefore,
                   EarliestUnfolded = unfolded
               };
    }

    #endregion // Methods
}