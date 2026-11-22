using Vandox.Backend.History;
using Vandox.Backend.Processes;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProcessesChartView"/>
/// </summary>
[TestClass]
public class ProcessesChartViewTests
{
    #region Constants

    private const string NoDataNote = "No process data is stored in this range.";
    private const string StartsBeforeNote = "This range starts before the oldest stored process data of this source.";
    private const string IncompleteNote = "Some snapshots in this range do not list every program.";
    private const string NotFoldedNote = "Values after 2026-10-01 10:20:00 UTC are not complete yet and may still change.";
    private const string CutListNote = "The list shows the first 256 sources.";
    private const string RssNote = "RSS counts shared memory in every program that maps it, so the stack can exceed the server's memory.";
    private const string StepNote = "Each point is the average of 10 minutes; the legend shows the minimum and maximum.";
    private const string OtherPrograms = "Other programs";
    private const char RightToLeftOverride = (char)0x202E;
    private const char Bell = (char)7;

    #endregion // Constants

    #region Fields

    private static readonly HistoryRange _range = new(HistorySamples.Base, HistorySamples.Base.AddMinutes(30));

    private static readonly TimeSpan _step = TimeSpan.FromMinutes(10);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The memory stack has one line per program in rank order and then the other programs, in MiB, with the stacked flag, the moment and one scale.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ProcessesChartViewBuildsTheMemoryStackOfTheProgramsAndOthersInMiB()
    {
        // Arrange
        var mebibyte = ProcessesSamples.Mebibyte;
        var moment = HistorySamples.Base.AddMinutes(5);
        var result = new ProgramRollupResult
                     {
                         Times = ProcessesSamples.Times(HistorySamples.Base, _step, 2),
                         Programs = [
                                        ProcessesSamples.Line("nginx", ProcessesSamples.Point(4 * mebibyte, 2 * mebibyte, 6 * mebibyte), ProcessesSamples.Point(8 * mebibyte, 4 * mebibyte, 12 * mebibyte)),
                                        ProcessesSamples.Line("mysqld", ProcessesSamples.Point(2 * mebibyte, mebibyte, 3 * mebibyte), ProcessesSamples.Point(mebibyte, 0, 2 * mebibyte))
                                    ],
                         Others = [mebibyte, 3 * mebibyte],
                         Incomplete = [0, 0],
                         MemoryKey = ProgramMemoryKey.Pss
                     };

        // Act
        var view = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, moment);

        // Assert
        Assert.IsTrue(view.Stacked, "the chart is stacked");
        Assert.AreSequenceEqual(result.Times.ToArray(), view.Times.ToArray(), "the step starts");
        Assert.AreEqual($"nginx|mysqld|{OtherPrograms}", string.Join('|', view.Lines.Select(line => line.Label)), "the programs in rank order, the other programs on top");
        Assert.AreEqual("0,0,0", string.Join(',', view.Lines.Select(line => line.Scale)), "every line is on scale 0");
        Assert.AreSequenceEqual<double?>([4.0, 8.0], view.Lines[0].Values, "the average of the first program in MiB");
        Assert.AreSequenceEqual<double?>([2.0, 1.0], view.Lines[1].Values, "the average of the second program in MiB");
        Assert.AreSequenceEqual<double?>([1.0, 3.0], view.Lines[2].Values, "the other programs in MiB");
        Assert.AreEqual(moment, view.Moment, "the moment inside the range is kept");
        Assert.IsEmpty(view.Marks, "no marks");
        Assert.IsEmpty(view.Events, "no events");
    }

    /// <summary>
    /// A moment outside the range is not marked.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ProcessesChartViewLeavesAMomentOutsideTheRangeUnmarked()
    {
        // Arrange
        var result = ProcessesSamples.Result(HistorySamples.Base, ProgramMemoryKey.Pss, "nginx");

        // Act
        var before = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, HistorySamples.Base.AddMinutes(-1));
        var after = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, HistorySamples.Base.AddMinutes(31));
        var none = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, null);

        // Assert
        Assert.IsNull(before.Moment, "before the range");
        Assert.IsNull(after.Moment, "after the range");
        Assert.IsNull(none.Moment, "no moment");
    }

    /// <summary>
    /// A PSS result is labelled on the axis as PSS in MiB and has no note.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ProcessesChartViewLabelsThePssAxis()
    {
        // Arrange
        var result = ProcessesSamples.Result(HistorySamples.Base, ProgramMemoryKey.Pss, "nginx");

        // Act
        var view = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, null);

        // Assert
        Assert.AreSequenceEqual(["Memory (PSS), MiB"], view.Axes.ToArray(), "the axis of a PSS range");
        Assert.IsEmpty(view.Notes, "no note for PSS");
    }

    /// <summary>
    /// An RSS result is labelled on the axis as RSS in MiB and carries the note about shared memory.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ProcessesChartViewAddsTheRssNoteForAnRssRange()
    {
        // Arrange
        var result = ProcessesSamples.Result(HistorySamples.Base, ProgramMemoryKey.Rss, "nginx");

        // Act
        var view = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, null);

        // Assert
        Assert.AreSequenceEqual(["Memory (RSS), MiB"], view.Axes.ToArray(), "the axis of an RSS range");
        Assert.AreSequenceEqual([RssNote], view.Notes.ToArray(), "the RSS note");
    }

    /// <summary>
    /// The CPU stack keeps the percent of the agent, has the axis CPU % and no note.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void ProcessesChartViewBuildsTheCpuStackWithoutNote()
    {
        // Arrange
        var result = new ProgramRollupResult
                     {
                         Times = ProcessesSamples.Times(HistorySamples.Base, _step, 2),
                         Programs = [
                                        ProcessesSamples.Line("php-fpm", ProcessesSamples.Point(150.5, 100, 200), ProcessesSamples.Point(50.25, 10, 90)),
                                        ProcessesSamples.Line("mysqld", ProcessesSamples.Point(20, 5, 30), ProcessesSamples.Point(10, 0, 15))
                                    ],
                         Others = [7.5, 2.5],
                         Incomplete = [0, 0]
                     };

        // Act
        var view = ProcessesChartView.Build(result, ProgramMeasure.Cpu, _range, null);

        // Assert
        Assert.IsTrue(view.Stacked, "the chart is stacked");
        Assert.AreSequenceEqual(["CPU %"], view.Axes.ToArray(), "the axis");
        Assert.IsEmpty(view.Notes, "no note");
        Assert.AreEqual($"php-fpm|mysqld|{OtherPrograms}", string.Join('|', view.Lines.Select(line => line.Label)), "the programs and the other programs");
        Assert.AreSequenceEqual<double?>([150.5, 50.25], view.Lines[0].Values, "the CPU percent is not converted");
        Assert.AreSequenceEqual<double?>([7.5, 2.5], view.Lines[2].Values, "the other programs");
        Assert.AreSequenceEqual<double?>([100.0, 10.0], view.Lines[0].Minimums, "the minimums are not converted");
    }

    /// <summary>
    /// A program carries its minimum and maximum per step, in MiB for memory, and the other programs carry none.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void ProcessesChartViewCarriesMinimumAndMaximumPerProgramButNotForOthers()
    {
        // Arrange
        var mebibyte = ProcessesSamples.Mebibyte;
        var result = new ProgramRollupResult
                     {
                         Times = ProcessesSamples.Times(HistorySamples.Base, _step, 2),
                         Programs = [ProcessesSamples.Line("nginx", ProcessesSamples.Point(4 * mebibyte, 0, 6 * mebibyte), ProcessesSamples.Point(8 * mebibyte, 4 * mebibyte, 12 * mebibyte))],
                         Others = [mebibyte, 3 * mebibyte],
                         Incomplete = [0, 0],
                         MemoryKey = ProgramMemoryKey.Pss
                     };

        // Act
        var view = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, null);

        // Assert
        Assert.AreSequenceEqual<double?>([0.0, 4.0], view.Lines[0].Minimums, "the minimums of the program in MiB, 0 where a snapshot does not name it");
        Assert.AreSequenceEqual<double?>([6.0, 12.0], view.Lines[0].Maximums, "the maximums of the program in MiB");
        Assert.AreEqual(OtherPrograms, view.Lines[1].Label, "the other programs");
        Assert.IsEmpty(view.Lines[1].Minimums, "the other programs have no minimum");
        Assert.IsEmpty(view.Lines[1].Maximums, "the other programs have no maximum");
    }

    /// <summary>
    /// A step without a folded snapshot is a gap in every line, the values and the extremes, and the other steps keep theirs.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ProcessesChartViewLeavesEveryLineNullAtAGapStep()
    {
        // Arrange
        var mebibyte = ProcessesSamples.Mebibyte;
        var point = ProcessesSamples.Point(mebibyte, mebibyte, mebibyte);
        var result = new ProgramRollupResult
                     {
                         Times = ProcessesSamples.Times(HistorySamples.Base, _step, 3),
                         Programs = [ProcessesSamples.Line("nginx", point, null, point), ProcessesSamples.Line("mysqld", point, null, point)],
                         Others = [mebibyte, null, mebibyte],
                         Incomplete = [0, 0, 0],
                         MemoryKey = ProgramMemoryKey.Pss
                     };

        // Act
        var view = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, null);

        // Assert
        Assert.HasCount(3, view.Lines, "two programs and the other programs");

        foreach (var line in view.Lines)
        {
            Assert.IsNull(line.Values[1], $"{line.Label}: the gap step is null");
            Assert.AreEqual(1.0, line.Values[0], $"{line.Label}: the step before the gap keeps its value");
            Assert.AreEqual(1.0, line.Values[2], $"{line.Label}: the step after the gap keeps its value");
        }

        Assert.IsNull(view.Lines[0].Minimums[1], "no minimum at the gap");
        Assert.IsNull(view.Lines[0].Maximums[1], "no maximum at the gap");
    }

    /// <summary>
    /// A hostile program name is shown escaped and cut at 64 characters in the label of its line.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void ProcessesChartViewEscapesAndCapsProgramLabels()
    {
        // Arrange
        var hostile = "evil" + RightToLeftOverride + "name" + Bell;
        var longName = new string('a', 100);
        var result = ProcessesSamples.Result(HistorySamples.Base, ProgramMemoryKey.Pss, hostile, longName);

        // Act
        var view = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, null);

        // Assert
        Assert.AreEqual("evil\\u202ename\\u0007", view.Lines[0].Label, "the control characters are written as escapes");
        Assert.AreEqual(new string('a', 64) + "[cut: 36 more characters]", view.Lines[1].Label, "the label is capped at 64 characters");
    }

    /// <summary>
    /// The note about missing data is added exactly when the result has no step, and then it stands alone.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ProcessesChartViewNotesAddsTheNoDataNoteOnlyForAnEmptyRange()
    {
        // Arrange
        var empty = new ProgramRollupResult();

        // Act
        var none = ProcessesChartView.Notes(empty, Sources(false), _range, _step);
        var data = ProcessesChartView.Notes(Clean(), Sources(false), _range, _step);

        // Assert
        Assert.AreSequenceEqual([NoDataNote], none.ToArray(), "an empty result gives only the note");
        Assert.DoesNotContain(NoDataNote, data.ToArray(), "a result with steps has no such note");
    }

    /// <summary>
    /// The note that the range starts before the oldest data is added exactly when the read says so.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ProcessesChartViewNotesAddsTheOldestDataNoteOnlyWhenTheRangeStartsBeforeTheBuckets()
    {
        // Arrange
        var before = Clean(startsBefore: true);

        // Act
        var with = ProcessesChartView.Notes(before, Sources(false), _range, _step);
        var without = ProcessesChartView.Notes(Clean(), Sources(false), _range, _step);

        // Assert
        Assert.AreSequenceEqual([StartsBeforeNote, StepNote], with.ToArray(), "the note, then the step note");
        Assert.DoesNotContain(StartsBeforeNote, without.ToArray(), "no note when the data covers the start");
    }

    /// <summary>
    /// The note about incomplete snapshots is added exactly when a step holds one.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ProcessesChartViewNotesAddsTheIncompleteSnapshotNoteOnlyWhenAStepHasOne()
    {
        // Arrange
        var incomplete = Clean(incomplete: [0, 2, 0]);
        var allComplete = Clean(incomplete: [0, 0, 0]);

        // Act
        var with = ProcessesChartView.Notes(incomplete, Sources(false), _range, _step);
        var without = ProcessesChartView.Notes(allComplete, Sources(false), _range, _step);

        // Assert
        Assert.AreSequenceEqual([IncompleteNote, StepNote], with.ToArray(), "the note, then the step note");
        Assert.DoesNotContain(IncompleteNote, without.ToArray(), "no note when every snapshot lists every program");
    }

    /// <summary>
    /// The note about values not folded yet names the time of the oldest unfolded snapshot and is added exactly when it lies before the end of the range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ProcessesChartViewNotesAddsTheNotFoldedNoteOnlyBeforeTheRangeEnd()
    {
        // Arrange
        var before = Clean(unfolded: HistorySamples.Base.AddMinutes(20));
        var atEnd = Clean(unfolded: _range.End);
        var after = Clean(unfolded: _range.End.AddMinutes(1));
        var none = Clean();

        // Act
        var notes = ProcessesChartView.Notes(before, Sources(false), _range, _step);

        // Assert
        Assert.AreSequenceEqual([NotFoldedNote, StepNote], notes.ToArray(), "the note with the time, then the step note");
        Assert.AreSequenceEqual([StepNote], ProcessesChartView.Notes(atEnd, Sources(false), _range, _step).ToArray(), "unfolded at the end of the range");
        Assert.AreSequenceEqual([StepNote], ProcessesChartView.Notes(after, Sources(false), _range, _step).ToArray(), "unfolded after the end of the range");
        Assert.AreSequenceEqual([StepNote], ProcessesChartView.Notes(none, Sources(false), _range, _step).ToArray(), "nothing unfolded");
    }

    /// <summary>
    /// A cut source list adds its note, and the notes come in the order list, start, incomplete, not folded, step.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void ProcessesChartViewNotesKeepsTheOrderOfTheNotes()
    {
        // Arrange
        var everything = Clean(startsBefore: true, incomplete: [1, 0, 0], unfolded: HistorySamples.Base.AddMinutes(20));

        // Act
        var all = ProcessesChartView.Notes(everything, Sources(true), _range, _step);
        var cutOnly = ProcessesChartView.Notes(Clean(), Sources(true), _range, _step);

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
    [DataRow(30, "30 minutes")]
    [DataRow(60, "1 hour")]
    [DataRow(720, "12 hours")]
    [DataRow(1440, "1 day")]
    [DataRow(4320, "3 days")]
    public void ProcessesChartViewWritesTheStepNote(int minutes, string text)
    {
        // Arrange
        var expected = $"Each point is the average of {text}; the legend shows the minimum and maximum.";

        // Act
        var notes = ProcessesChartView.Notes(Clean(), Sources(false), _range, TimeSpan.FromMinutes(minutes));

        // Assert
        Assert.AreSequenceEqual([expected], notes.ToArray(), "the step note alone");
    }

    /// <summary>
    /// Programs and others cut by the caps, as a full list of ten programs with a non-zero rest is, give no note of their own.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void ProcessesChartViewAddsNoNoteForCutPrograms()
    {
        // Arrange
        var programs = Enumerable.Range(1, ProcessesView.Programs).Select(index => $"program{index}").ToArray();
        var result = ProcessesSamples.Result(HistorySamples.Base, ProgramMemoryKey.Pss, programs);

        // Act
        var notes = ProcessesChartView.Notes(result, Sources(false), _range, _step);
        var view = ProcessesChartView.Build(result, ProgramMeasure.Memory, _range, null);

        // Assert
        Assert.AreSequenceEqual([StepNote], notes.ToArray(), "only the step note");
        Assert.HasCount(ProcessesView.Programs + 1, view.Lines, "ten programs and the other programs");
        Assert.IsEmpty(view.Notes, "no note on the chart");
    }

    /// <summary>
    /// Creates the list of sources of a read.
    /// </summary>
    /// <param name="more">Whether the list is cut</param>
    /// <returns>The list</returns>
    private static ProgramSourceList Sources(bool more)
    {
        return ProcessesSamples.SourceList(more, ("agent", HistorySamples.Base.AddMinutes(20)));
    }

    /// <summary>
    /// Creates a result of three steps that calls for no note.
    /// </summary>
    /// <param name="startsBefore">Whether the read says the range starts before the oldest bucket</param>
    /// <param name="incomplete">The incomplete counts per step, or <c>null</c> for none</param>
    /// <param name="unfolded">The time of the oldest unfolded snapshot</param>
    /// <returns>The result</returns>
    private static ProgramRollupResult Clean(bool startsBefore = false, int[]? incomplete = null, DateTimeOffset? unfolded = null)
    {
        var mebibyte = ProcessesSamples.Mebibyte;
        var point = ProcessesSamples.Point(mebibyte, mebibyte, mebibyte);

        return new ProgramRollupResult
               {
                   Times = ProcessesSamples.Times(HistorySamples.Base, _step, 3),
                   Programs = [ProcessesSamples.Line("nginx", point, point, point)],
                   Others = [mebibyte, mebibyte, mebibyte],
                   Incomplete = incomplete ?? [0, 0, 0],
                   MemoryKey = ProgramMemoryKey.Pss,
                   StartsBeforeBuckets = startsBefore,
                   EarliestUnfolded = unfolded
               };
    }

    #endregion // Methods
}