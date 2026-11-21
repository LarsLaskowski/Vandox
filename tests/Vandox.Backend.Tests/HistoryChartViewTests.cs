using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryChartView"/>
/// </summary>
[TestClass]
public class HistoryChartViewTests
{
    #region Fields

    private static readonly HistoryRange _range = new(HistorySamples.Base, HistorySamples.Base.AddHours(1));

    private static readonly SeriesKey _cpu = new("system", "cpu_percent");

    private static readonly SeriesKey _memory = new("system", "mem_available_bytes");

    private static readonly SeriesKey _load = new("system", "load1");

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Every line gets the scale of its metric's place in the address, and the first two metrics give the drawn axes.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void FromGivesEachMetricItsScaleAndTheFirstTwoAxes()
    {
        // Arrange
        var result = Result(Raw([Line(_cpu, "mode=user", 1, 2, 3), Line(_cpu, "mode=idle", 4, 5, 6), Line(_memory, string.Empty, 7, 8, 9), Line(_load, string.Empty, 1, 1, 1)]));

        // Act
        var view = HistoryChartView.From(result, [_cpu, _memory, _load], _range, null);

        // Assert
        Assert.AreEqual("0,0,1,2", string.Join(',', view.Lines.Select(line => line.Scale)), "scale of each line is the index of its metric");
        Assert.AreEqual("system cpu_percent|system mem_available_bytes", string.Join('|', view.Axes), "axes of the first two metrics");
        Assert.HasCount(3, view.Times, "the axis times");
        Assert.AreEqual(HistorySamples.Base, view.Times[0], "first time");
    }

    /// <summary>
    /// With one metric there is one axis, and with no line there is none.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void FromDrawsOneAxisForOneMetric()
    {
        // Arrange
        var one = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]));
        var none = Result(Raw([]));

        // Act
        var single = HistoryChartView.From(one, [_cpu], _range, null);
        var empty = HistoryChartView.From(none, [], _range, null);

        // Assert
        Assert.AreEqual("system cpu_percent", string.Join('|', single.Axes), "one axis");
        Assert.IsEmpty(empty.Axes, "no axis without metrics");
        Assert.IsEmpty(empty.Lines, "no line without data");
    }

    /// <summary>
    /// A line is named with its source, name and labels, in the order of the result.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void FromNamesLinesWithSourceNameAndLabels()
    {
        // Arrange
        var result = Result(Raw([Line(_cpu, "mode=user", 1, 2, 3), Line(_cpu, "mode=idle", 4, 5, 6), Line(_load, string.Empty, 1, 1, 1)]));

        // Act
        var view = HistoryChartView.From(result, [_cpu, _load], _range, null);

        // Assert
        Assert.AreEqual("system cpu_percent mode=user", view.Lines[0].Label, "first line");
        Assert.AreEqual("system cpu_percent mode=idle", view.Lines[1].Label, "second line");
        Assert.AreEqual("system load1", view.Lines[2].Label.TrimEnd(), "a line without labels");
    }

    /// <summary>
    /// A label is escaped and capped at 64 characters, and so are the axis labels.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void FromEscapesAndCapsLabels()
    {
        // Arrange
        var rightToLeft = ((char)0x202E).ToString();
        var key = new SeriesKey($"sys{rightToLeft}tem", "cpu");
        var longLabels = $"mode={new string('x', 100)}";
        var result = Result(Raw([Line(key, $"role={rightToLeft}admin", 1, 2, 3), Line(key, longLabels, 1, 2, 3)]));

        // Act
        var view = HistoryChartView.From(result, [key], _range, null);

        // Assert
        Assert.AreEqual(LogText.Escape($"sys{rightToLeft}tem cpu role={rightToLeft}admin", HistoryView.LabelChars), view.Lines[0].Label, "an escaped label");
        Assert.DoesNotContain(rightToLeft, view.Lines[0].Label, "no raw bidirectional control character");
        Assert.AreEqual(LogText.Escape($"sys{rightToLeft}tem cpu {longLabels}", HistoryView.LabelChars), view.Lines[1].Label, "a long label is capped as the display caps it");
        Assert.AreEqual(LogText.Escape($"sys{rightToLeft}tem cpu", HistoryView.LabelChars), view.Axes[0], "the axis label is escaped");
    }

    /// <summary>
    /// Colors repeat every six lines and the lines from the seventh on are dashed.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void FromAssignsColorsAndDashesFromTheSeventhLine()
    {
        // Arrange
        var lines = Enumerable.Range(0, 8).Select(index => Line(_cpu, $"cpu={index}", 1, 2, 3)).ToList();

        // Act
        var view = HistoryChartView.From(Result(Raw(lines)), [_cpu], _range, null);

        // Assert
        Assert.AreEqual("0,1,2,3,4,5,0,1", string.Join(',', view.Lines.Select(line => line.Color)), "colors in order, repeated");
        Assert.AreEqual("False,False,False,False,False,False,True,True", string.Join(',', view.Lines.Select(line => line.Dashed)), "dashed from the seventh line");
    }

    /// <summary>
    /// A gap is no value and a point between samples is flagged as bridged.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void FromKeepsGapsAndBridges()
    {
        // Arrange
        var points = new[]
                     {
                         Point(PointState.Value, 5),
                         Point(PointState.Gap, 0),
                         Point(PointState.BetweenSamples, 0),
                         Point(PointState.Value, 8)
                     };
        var result = new TimeSeriesResult
                     {
                         Resolution = TimeSeriesResolution.Raw,
                         Times = [HistorySamples.Base, HistorySamples.Base.AddMinutes(10), HistorySamples.Base.AddMinutes(20), HistorySamples.Base.AddMinutes(30)],
                         Lines = [
                                     new SeriesLine
                                     {
                                         Source = "system",
                                         Name = "cpu_percent",
                                         Points = points
                                     }
                                 ]
                     };

        // Act
        var line = HistoryChartView.From(result, [_cpu], _range, null).Lines[0];

        // Assert
        Assert.AreEqual(5d, line.Values[0], "a value");
        Assert.IsNull(line.Values[1], "a gap breaks the line");
        Assert.AreEqual(8d, line.Values[3], "the value after");
        Assert.AreEqual("False,False,True,False", string.Join(',', line.Bridged), "only the point between samples is bridged");
        Assert.HasCount(4, line.Values, "one value per time");
    }

    /// <summary>
    /// At a step the values are the averages, the extremes are carried, and the step note says so.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void FromAtStepCarriesExtremesAndStepNote()
    {
        // Arrange
        var line = new SeriesLine
                   {
                       Source = "system",
                       Name = "cpu_percent",
                       Points = [
                                    StepPoint(1, 2, 4),
                                    StepPoint(2, 3, 9),
                                    new SeriesPoint
                                    {
                                        State = PointState.Gap
                                    }
                                ]
                   };
        var result = new TimeSeriesResult
                     {
                         Resolution = TimeSeriesResolution.Step,
                         Step = TimeSpan.FromMinutes(5),
                         Times = [HistorySamples.Base, HistorySamples.Base.AddMinutes(5), HistorySamples.Base.AddMinutes(10)],
                         Lines = [line]
                     };

        // Act
        var view = HistoryChartView.From(result, [_cpu], _range, null);

        // Assert
        Assert.AreEqual("2,3", string.Join(',', view.Lines[0].Values.Take(2)), "the averages are the values");
        Assert.IsNull(view.Lines[0].Values[2], "a gap");
        Assert.AreEqual("1,2", string.Join(',', view.Lines[0].Minimums.Take(2)), "the minimums");
        Assert.AreEqual("4,9", string.Join(',', view.Lines[0].Maximums.Take(2)), "the maximums");
        Assert.IsNull(view.Lines[0].Minimums[2], "no minimum in a gap");
        Assert.IsNull(view.Lines[0].Maximums[2], "no maximum in a gap");
        Assert.AreEqual("Each point is the average of 5 minutes; the legend shows the minimum and maximum.", view.StepNote, "the text of the note");
    }

    /// <summary>
    /// At raw resolution there are no extremes and no step note.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void FromAtRawHasNoExtremesAndNoNote()
    {
        // Arrange
        var result = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]));

        // Act
        var view = HistoryChartView.From(result, [_cpu], _range, null);

        // Assert
        Assert.IsEmpty(view.Lines[0].Minimums, "no minimums at raw resolution");
        Assert.IsEmpty(view.Lines[0].Maximums, "no maximums at raw resolution");
        Assert.AreEqual(string.Empty, view.StepNote, "no step note");
        Assert.AreEqual("1,2,3", string.Join(',', view.Lines[0].Values), "the values are the raw values");
    }

    /// <summary>
    /// Kills, boots and restarts are marks at their time and a backup run is a span; a run without an end spans to the end of the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void FromMarksKillsBootsRestartsAndBackupSpans()
    {
        // Arrange
        var ended = Base(10);
        var markers = new[]
                      {
                          Marker(MarkerKind.OomKill, 5, "php-fpm", 4711),
                          Marker(MarkerKind.Boot, 6, string.Empty, null),
                          Marker(MarkerKind.ServiceRestart, 7, "nginx.service", null),
                          new SeriesMarker
                          {
                              Kind = MarkerKind.BackupRun,
                              At = Base(8),
                              EndsAt = ended,
                              End = BackupRunEnd.Ended,
                              Text = "pzstd"
                          },
                          new SeriesMarker
                          {
                              Kind = MarkerKind.BackupRun,
                              At = Base(20),
                              End = BackupRunEnd.Running,
                              Text = "pzstd"
                          },
                          new SeriesMarker
                          {
                              Kind = MarkerKind.BackupRun,
                              At = Base(30),
                              End = BackupRunEnd.Unknown,
                              Text = "pzstd"
                          }
                      };

        // Act
        var view = HistoryChartView.From(Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), markers), [_cpu], _range, null);

        // Assert
        Assert.AreEqual("OomKill,Boot,ServiceRestart,BackupRun,BackupRun,BackupRun", string.Join(',', view.Marks.Select(mark => mark.Kind)), "kinds in order");
        Assert.AreEqual(Base(5), view.Marks[0].At, "kill time");
        Assert.IsNull(view.Marks[0].EndsAt, "a kill has no end");
        Assert.IsNull(view.Marks[1].EndsAt, "a boot has no end");
        Assert.IsNull(view.Marks[2].EndsAt, "a restart has no end");
        Assert.AreEqual(Base(8), view.Marks[3].At, "backup start");
        Assert.AreEqual(ended, view.Marks[3].EndsAt, "backup end");
        Assert.AreEqual(_range.End, view.Marks[4].EndsAt, "a running backup spans to the end of the range");
        Assert.AreEqual(_range.End, view.Marks[5].EndsAt, "a backup with an unknown end spans to the end of the range");
    }

    /// <summary>
    /// The events are listed with their time, kind and text.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void FromListsEventsWithKindAndText()
    {
        // Arrange
        var markers = new[]
                      {
                          Marker(MarkerKind.OomKill, 5, "php-fpm", 4711),
                          Marker(MarkerKind.Boot, 6, string.Empty, null, "0123abcd-0123-0123-0123-0123456789ab"),
                          Marker(MarkerKind.ServiceRestart, 7, "nginx.service", null),
                          new SeriesMarker
                          {
                              Kind = MarkerKind.BackupRun,
                              At = Base(8),
                              EndsAt = new DateTimeOffset(2026, 10, 1, 10, 12, 30, TimeSpan.Zero),
                              End = BackupRunEnd.Ended,
                              Text = "pzstd"
                          },
                          new SeriesMarker
                          {
                              Kind = MarkerKind.BackupRun,
                              At = Base(20),
                              End = BackupRunEnd.Running,
                              Text = "pzstd"
                          },
                          new SeriesMarker
                          {
                              Kind = MarkerKind.BackupRun,
                              At = Base(30),
                              End = BackupRunEnd.Unknown,
                              Text = "pzstd"
                          }
                      };

        // Act
        var events = HistoryChartView.From(Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), markers), [_cpu], _range, null).Events;

        // Assert
        Assert.HasCount(6, events, "one row per marker");
        Assert.AreEqual("OOM kill|Boot|Service restart|Backup run|Backup run|Backup run", string.Join('|', events.Select(row => row.Kind)), "kind texts");
        Assert.AreEqual(Base(5), events[0].At, "event time");
        Assert.AreEqual("php-fpm (PID 4711)", HistorySamples.Text(events[0].Text), "kill text");
        Assert.AreEqual("0123abcd-0123-0123-0123-0123456789ab", HistorySamples.Text(events[1].Text), "boot ID");
        Assert.AreEqual("nginx.service", HistorySamples.Text(events[2].Text), "unit");
        Assert.AreEqual("pzstd until 2026-10-01 10:12:30 UTC", HistorySamples.Text(events[3].Text), "backup with its end");
        Assert.AreEqual("pzstd, running", HistorySamples.Text(events[4].Text), "running backup");
        Assert.AreEqual("pzstd, end unknown", HistorySamples.Text(events[5].Text), "backup with an unknown end");
    }

    /// <summary>
    /// An event text is capped at 256 characters in at most 16 runs, as the display caps it.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void FromCapsEventTexts()
    {
        // Arrange
        var command = string.Concat(Enumerable.Repeat("a\u202E", 150));
        var markers = new[] { Marker(MarkerKind.OomKill, 5, command, 12) };

        // Act
        var runs = HistoryChartView.From(Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), markers), [_cpu], _range, null).Events[0].Text;

        // Assert
        var expected = LogText.Runs($"{command} (PID 12)", HistoryView.TextChars, HistoryView.TextRuns);

        Assert.IsLessThanOrEqualTo(HistoryView.TextRuns, runs.Count, "at most 16 runs");
        Assert.HasCount(expected.Count, runs, "the runs of the display");
        Assert.AreEqual(string.Join('|', expected.Select(run => $"{run.Escaped}:{run.Text}")), string.Join('|', runs.Select(run => $"{run.Escaped}:{run.Text}")), "the same runs as the display builds");
        Assert.DoesNotContain("\u202E", string.Concat(runs.Where(run => run.Escaped == false).Select(run => run.Text)), "the control character is only in escaped form");
    }

    /// <summary>
    /// When the query cut the event list, the note names the time after which events are not shown.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void FromShowsEventsCutText()
    {
        // Arrange
        var cutAt = Base(40);
        var result = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), markersCompleteUntil: cutAt);

        // Act
        var cut = HistoryChartView.From(result, [_cpu], _range, null);
        var complete = HistoryChartView.From(Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), markersCompleteUntil: _range.End), [_cpu], _range, null);

        // Assert
        Assert.AreEqual("Events after 2026-10-01 10:40:00 UTC are not shown.", string.Join('|', cut.Notes), "the cut note");
        Assert.IsEmpty(complete.Notes, "no note when the events are complete to the end of the range");
    }

    /// <summary>
    /// A chosen metric without a line gets the no-values note, and one the query cut does not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void FromNotesMetricWithoutValues()
    {
        // Arrange
        var result = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]));

        // Act
        var view = HistoryChartView.From(result, [_cpu, _memory, _load], _range, null);

        // Assert
        Assert.AreEqual("system mem_available_bytes has no values in this range.|system load1 has no values in this range.", string.Join('|', view.Notes), "one note per metric without values, in the order of the address");
    }

    /// <summary>
    /// Cut lines are named in their note, and a cut metric gets no no-values note.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void FromNotesCutMetricsWithoutNoValuesText()
    {
        // Arrange
        var result = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), cut: [_memory, _load]);

        // Act
        var view = HistoryChartView.From(result, [_cpu, _memory, _load], _range, null);

        // Assert
        Assert.HasCount(1, view.Notes, "only the cut note");
        Assert.StartsWith("Only 64 lines can be drawn; lines of ", view.Notes[0], "the cut note");
        Assert.EndsWith(" are left out.", view.Notes[0], "the end of the cut note");
        Assert.Contains("system mem_available_bytes", view.Notes[0], "names a cut metric");
        Assert.Contains("system load1", view.Notes[0], "names the other cut metric");
        Assert.DoesNotContain("system cpu_percent", view.Notes[0], "a metric that was not cut is not named");
    }

    /// <summary>
    /// A range that starts before the kept rollups gets its note.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void FromNotesRangeBeforeRollups()
    {
        // Arrange
        var result = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), startsBeforeRollups: true);

        // Act
        var view = HistoryChartView.From(result, [_cpu], _range, null);

        // Assert
        Assert.AreEqual("This range starts before the oldest kept rollups; older values are no longer stored.", string.Join('|', view.Notes), "the note");
    }

    /// <summary>
    /// Values after the completeness time are noted, and the notes come in the order no values, cut lines, rollups, incomplete, cut events.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void FromNotesIncompletePoints()
    {
        // Arrange
        var incomplete = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), completeUntil: Base(45));
        var everything = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), cut: [_load], startsBeforeRollups: true, completeUntil: Base(45), markersCompleteUntil: Base(50));

        // Act
        var single = HistoryChartView.From(incomplete, [_cpu], _range, null);
        var all = HistoryChartView.From(everything, [_cpu, _memory, _load], _range, null);

        // Assert
        Assert.AreEqual("Values after 2026-10-01 10:45:00 UTC are not complete yet and may still change.", string.Join('|', single.Notes), "the note");
        Assert.HasCount(5, all.Notes, "five notes");
        Assert.AreEqual("system mem_available_bytes has no values in this range.", all.Notes[0], "no values first");
        Assert.StartsWith("Only 64 lines", all.Notes[1], "cut lines second");
        Assert.StartsWith("This range starts before", all.Notes[2], "rollups third");
        Assert.StartsWith("Values after", all.Notes[3], "incomplete values fourth");
        Assert.StartsWith("Events after", all.Notes[4], "cut events last");
    }

    /// <summary>
    /// A complete result has no note, also when the completeness time is the end of the range or later.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void FromHasNoNotesForACompleteResult()
    {
        // Arrange
        var complete = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]), completeUntil: _range.End, markersCompleteUntil: _range.End.AddHours(1));
        var unknown = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]));

        // Act
        var first = HistoryChartView.From(complete, [_cpu], _range, null);
        var second = HistoryChartView.From(unknown, [_cpu], _range, null);

        // Assert
        Assert.IsEmpty(first.Notes, "completeness up to the end of the range");
        Assert.IsEmpty(second.Notes, "no completeness times");
    }

    /// <summary>
    /// The moment is carried only when it lies inside the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void FromMarksTheMomentOnlyInsideTheRange()
    {
        // Arrange
        var result = Result(Raw([Line(_cpu, string.Empty, 1, 2, 3)]));

        // Act
        var inside = HistoryChartView.From(result, [_cpu], _range, Base(30));
        var atStart = HistoryChartView.From(result, [_cpu], _range, _range.Start);
        var before = HistoryChartView.From(result, [_cpu], _range, _range.Start.AddSeconds(-1));
        var after = HistoryChartView.From(result, [_cpu], _range, _range.End.AddSeconds(1));
        var none = HistoryChartView.From(result, [_cpu], _range, null);

        // Assert
        Assert.AreEqual(Base(30), inside.Moment, "a moment inside the range");
        Assert.AreEqual(_range.Start, atStart.Moment, "a moment at the start");
        Assert.IsNull(before.Moment, "before the range");
        Assert.IsNull(after.Moment, "after the range");
        Assert.IsNull(none.Moment, "no moment");
    }

    /// <summary>
    /// 64 lines of 1,999 times and 1,000 markers with long texts stay within the caps of the display.
    /// </summary>
    [TestMethod]
    [TestCategory("AC11")]
    public void FromStaysWithinTheCaps()
    {
        // Arrange
        var times = Enumerable.Range(0, 1999).Select(index => HistorySamples.Base.AddSeconds(index)).ToList();
        var points = Enumerable.Range(0, 1999).Select(index => Point(PointState.Value, index)).ToList();
        var lines = Enumerable.Range(0, 64).Select(index => new SeriesLine
                                                            {
                                                                Source = "system",
                                                                Name = "cpu_percent",
                                                                Labels = $"cpu={index}" + new string('x', 200),
                                                                Points = points
                                                            }).ToList();
        var markers = Enumerable.Range(0, 1000).Select(index => new SeriesMarker
                                                                {
                                                                    Kind = MarkerKind.OomKill,
                                                                    At = HistorySamples.Base.AddSeconds(index),
                                                                    Text = new string('c', 300) + "\u202E",
                                                                    Pid = index + 1
                                                                }).ToList();
        var result = new TimeSeriesResult
                     {
                         Resolution = TimeSeriesResolution.Raw,
                         Times = times,
                         Lines = lines,
                         Markers = markers
                     };

        // Act
        var view = HistoryChartView.From(result, [_cpu], new HistoryRange(HistorySamples.Base, HistorySamples.Base.AddHours(1)), null);

        // Assert
        Assert.HasCount(64, view.Lines, "64 lines");
        Assert.IsTrue(view.Lines.All(line => line.Values.Count == 1999), "1,999 values per line");
        Assert.HasCount(1999, view.Times, "1,999 times");
        Assert.HasCount(1000, view.Marks, "1,000 marks");
        Assert.HasCount(1000, view.Events, "1,000 events");
        Assert.IsTrue(view.Events.All(row => row.Text.Count <= HistoryView.TextRuns), "at most 16 runs per event");
        Assert.IsTrue(view.Events.All(row => row.Text.Where(run => run.Escaped == false).Sum(run => run.Text.Length) <= HistoryView.TextChars), "at most 256 plain characters per event");
        Assert.IsTrue(Enumerable.Range(0, 64).All(index => view.Lines[index].Label == LogText.Escape($"system cpu_percent {lines[index].Labels}", HistoryView.LabelChars)), "labels are capped as the display caps them");
    }

    /// <summary>
    /// Marking another moment keeps the stacked flag of the view, and the other parts of the view, and leaves a view that is not stacked unstacked.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void HistoryChartViewWithMomentKeepsStacked()
    {
        // Arrange
        var line = new HistoryLine
                   {
                       Label = "nginx",
                       Values = [1.0, 2.0, 3.0]
                   };
        var stacked = new HistoryChartView
                      {
                          Times = [HistorySamples.Base, HistorySamples.Base.AddMinutes(20), HistorySamples.Base.AddMinutes(40)],
                          Lines = [line],
                          Axes = ["Memory (PSS), MiB"],
                          Notes = ["a note"],
                          StepNote = "a step note",
                          Stacked = true
                      };
        var plain = new HistoryChartView
                    {
                        Times = stacked.Times,
                        Lines = stacked.Lines
                    };
        var moment = HistorySamples.Base.AddMinutes(10);

        // Act
        var marked = stacked.WithMoment(moment, _range);
        var outside = stacked.WithMoment(HistorySamples.Base.AddHours(2), _range);
        var unmarked = plain.WithMoment(moment, _range);

        // Assert
        Assert.IsTrue(marked.Stacked, "the marked view is stacked");
        Assert.IsTrue(outside.Stacked, "a moment outside the range keeps the flag too");
        Assert.IsFalse(unmarked.Stacked, "a view that is not stacked stays so");
        Assert.AreEqual(moment, marked.Moment, "the moment inside the range");
        Assert.IsNull(outside.Moment, "no moment outside the range");
        Assert.AreSame(stacked.Lines, marked.Lines, "the lines are kept");
        Assert.AreSequenceEqual(["Memory (PSS), MiB"], marked.Axes.ToArray(), "the axes are kept");
        Assert.AreSequenceEqual(["a note"], marked.Notes.ToArray(), "the notes are kept");
        Assert.AreEqual("a step note", marked.StepNote, "the step note is kept");
    }

    /// <summary>
    /// Gives the lines of a result at raw resolution, the resolution of <see cref="Result"/>.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The same lines</returns>
    private static IReadOnlyList<SeriesLine> Raw(IReadOnlyList<SeriesLine> lines)
    {
        return lines;
    }

    /// <summary>
    /// Creates a time-series result of three times.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <param name="markers">The markers</param>
    /// <param name="cut">The metrics the query cut</param>
    /// <param name="startsBeforeRollups">Whether the range starts before the kept rollups</param>
    /// <param name="completeUntil">The completeness time of the values</param>
    /// <param name="markersCompleteUntil">The completeness time of the markers</param>
    /// <returns>The result</returns>
    private static TimeSeriesResult Result(IReadOnlyList<SeriesLine> lines, IReadOnlyList<SeriesMarker>? markers = null, IReadOnlyList<SeriesKey>? cut = null, bool startsBeforeRollups = false, DateTimeOffset? completeUntil = null, DateTimeOffset? markersCompleteUntil = null)
    {
        return new TimeSeriesResult
               {
                   Resolution = TimeSeriesResolution.Raw,
                   Times = [HistorySamples.Base, HistorySamples.Base.AddMinutes(20), HistorySamples.Base.AddMinutes(40)],
                   Lines = lines,
                   Markers = markers ?? [],
                   CutSeries = cut ?? [],
                   StartsBeforeRollups = startsBeforeRollups,
                   CompleteUntil = completeUntil,
                   MarkersCompleteUntil = markersCompleteUntil
               };
    }

    /// <summary>
    /// Creates a line of three raw values.
    /// </summary>
    /// <param name="key">The metric</param>
    /// <param name="labels">The labels</param>
    /// <param name="first">The first value</param>
    /// <param name="second">The second value</param>
    /// <param name="third">The third value</param>
    /// <returns>The line</returns>
    private static SeriesLine Line(SeriesKey key, string labels, double first, double second, double third)
    {
        return new SeriesLine
               {
                   Source = key.Source,
                   Name = key.Name,
                   Labels = labels,
                   Points = [Point(PointState.Value, first), Point(PointState.Value, second), Point(PointState.Value, third)]
               };
    }

    /// <summary>
    /// Creates a point whose minimum, average, maximum and last value are the same.
    /// </summary>
    /// <param name="state">The state</param>
    /// <param name="value">The value</param>
    /// <returns>The point</returns>
    private static SeriesPoint Point(PointState state, double value)
    {
        return new SeriesPoint
               {
                   State = state,
                   Minimum = value,
                   Average = value,
                   Maximum = value,
                   Last = value,
                   LastAt = HistorySamples.Base,
                   Count = 1
               };
    }

    /// <summary>
    /// Creates a point of a step.
    /// </summary>
    /// <param name="minimum">The minimum</param>
    /// <param name="average">The average</param>
    /// <param name="maximum">The maximum</param>
    /// <returns>The point</returns>
    private static SeriesPoint StepPoint(double minimum, double average, double maximum)
    {
        return new SeriesPoint
               {
                   State = PointState.Value,
                   Minimum = minimum,
                   Average = average,
                   Maximum = maximum,
                   Last = average,
                   LastAt = HistorySamples.Base,
                   Count = 5
               };
    }

    /// <summary>
    /// Gets a time some minutes after the base.
    /// </summary>
    /// <param name="minutes">The minutes</param>
    /// <returns>The time</returns>
    private static DateTimeOffset Base(int minutes)
    {
        return HistorySamples.Base.AddMinutes(minutes);
    }

    /// <summary>
    /// Creates a marker.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="minutes">The minutes after the base</param>
    /// <param name="text">The text</param>
    /// <param name="pid">The process ID</param>
    /// <param name="bootId">The boot ID</param>
    /// <returns>The marker</returns>
    private static SeriesMarker Marker(MarkerKind kind, int minutes, string text, long? pid, string bootId = "")
    {
        return new SeriesMarker
               {
                   Kind = kind,
                   At = Base(minutes),
                   Text = text,
                   Pid = pid,
                   BootId = bootId
               };
    }

    #endregion // Methods
}