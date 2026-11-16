using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.Overview;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProcessSnapshotView"/>
/// </summary>
[TestClass]
public class ProcessSnapshotViewTests
{
    #region Constants

    private const ulong Mebibyte = 1024 * 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// At most 20 programs and 20 processes are listed, the ones with the most memory first and ties by name or process ID, with the number of the others.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void FromListsTwentyProgramsAndProcessesByMemory()
    {
        // Arrange
        var programs = Enumerable.Range(0, 25).Select(index => HistorySamples.Program($"p{index:00}", 2, 12.5, (ulong)(index + 1) * Mebibyte)).ToList();

        programs.Add(HistorySamples.Program("zeta", 1, 0.04, 100 * Mebibyte));
        programs.Add(HistorySamples.Program("alpha", 3, 7, 100 * Mebibyte));

        var processes = Enumerable.Range(0, 22).Select(index => Process(100 + index, "root", $"cmd{index}", (ulong)(index + 1) * Mebibyte)).ToList();

        processes.Add(Process(500, "root", "tie-high", 1000 * Mebibyte));
        processes.Add(Process(400, "root", "tie-low", 1000 * Mebibyte));

        var snapshot = new ProcessSnapshot
                       {
                           Complete = true,
                           Programs = programs,
                           Processes = processes
                       };

        // Act
        var view = ProcessSnapshotView.FromProcesses(HistorySamples.Record(5, "agent", HistorySamples.Base, snapshot));

        // Assert
        Assert.HasCount(20, view.Programs.Rows, "20 programs");
        Assert.AreEqual($"alpha|3|7.0|{OverviewText.Size(100 * Mebibyte)}", Cells(view.Programs.Rows[0]), "the first program: its name, processes, CPU with one decimal and memory");
        Assert.AreEqual($"zeta|1|0.0|{OverviewText.Size(100 * Mebibyte)}", Cells(view.Programs.Rows[1]), "a tie in memory is ordered by name");
        Assert.AreEqual("p24", Text(view.Programs.Rows[2], 0), "then the next most memory");
        Assert.AreEqual("p07", Text(view.Programs.Rows[19], 0), "the twentieth program");
        Assert.AreEqual("and 7 more programs", view.Programs.More, "the programs beyond 20");
        Assert.HasCount(20, view.Processes.Rows, "20 processes");
        Assert.AreEqual($"400|root|tie-low|12.5|{OverviewText.Size(1000 * Mebibyte)}", Cells(view.Processes.Rows[0]), "the first process; a tie in memory is ordered by process ID");
        Assert.AreEqual("500", Text(view.Processes.Rows[1], 0), "the other of the tie");
        Assert.AreEqual("121", Text(view.Processes.Rows[2], 0), "then the most memory");
        Assert.AreEqual("and 4 more processes", view.Processes.More, "the processes beyond 20");
        Assert.AreEqual(HistorySamples.Base, view.CapturedAt, "the capture time");
        Assert.HasCount(4, view.Programs.Columns, "program, processes, CPU, memory");
        Assert.HasCount(5, view.Processes.Columns, "PID, user, command, CPU, memory");
    }

    /// <summary>
    /// The listed processes are counted against the total when the total is higher, and the number of rows beyond 20 is empty at 20 or fewer.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void FromCountsMoreAndListedOfTotal()
    {
        // Arrange
        var processes = Enumerable.Range(0, 25).Select(index => Process(100 + index, "root", "cmd", Mebibyte)).ToList();
        var twenty = Enumerable.Range(0, 20).Select(index => HistorySamples.Program($"p{index}", 1, 1, Mebibyte)).ToList();

        // Act
        var higher = ProcessSnapshotView.FromProcesses(Snapshot(new ProcessSnapshot
                                                                {
                                                                    Complete = false,
                                                                    Total = 300,
                                                                    Processes = processes,
                                                                    Programs = twenty
                                                                }));
        var equal = ProcessSnapshotView.FromProcesses(Snapshot(new ProcessSnapshot
                                                               {
                                                                   Complete = true,
                                                                   Total = 25,
                                                                   Processes = processes,
                                                                   Programs = twenty
                                                               }));
        var none = ProcessSnapshotView.FromProcesses(Snapshot(new ProcessSnapshot
                                                              {
                                                                  Complete = true,
                                                                  Processes = processes,
                                                                  Programs = twenty
                                                              }));

        // Assert
        Assert.AreEqual("25 of 300 processes are listed.", higher.ListedOfTotal, "the listed and the total");
        Assert.AreEqual(string.Empty, equal.ListedOfTotal, "the total is not higher than the list");
        Assert.AreEqual(string.Empty, none.ListedOfTotal, "no total");
        Assert.AreEqual(string.Empty, higher.Programs.More, "exactly 20 programs have no more");
        Assert.AreEqual("and 5 more processes", higher.Processes.More, "25 processes");
    }

    /// <summary>
    /// The memory is PSS only when every entry of that list has it; otherwise it is RSS, and the column says which.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void FromUsesPssOnlyWhenEveryEntryHasIt()
    {
        // Arrange
        var allPss = new ProcessSnapshot
                     {
                         Complete = true,
                         Programs = [Aggregate("big-rss", 900 * Mebibyte, 10 * Mebibyte), Aggregate("big-pss", 100 * Mebibyte, 80 * Mebibyte)],
                         Processes = [Sample(1, "a", 900 * Mebibyte, 10 * Mebibyte), Sample(2, "b", 100 * Mebibyte, 80 * Mebibyte)]
                     };
        var someMissing = new ProcessSnapshot
                          {
                              Complete = true,
                              Programs = [Aggregate("big-rss", 900 * Mebibyte, 10 * Mebibyte), Aggregate("no-pss", 100 * Mebibyte, null)],
                              Processes = [Sample(1, "a", 900 * Mebibyte, 10 * Mebibyte), Sample(2, "b", 100 * Mebibyte, null)]
                          };
        var mixed = new ProcessSnapshot
                    {
                        Complete = true,
                        Programs = [Aggregate("big-rss", 900 * Mebibyte, 10 * Mebibyte), Aggregate("big-pss", 100 * Mebibyte, 80 * Mebibyte)],
                        Processes = [Sample(1, "a", 900 * Mebibyte, 10 * Mebibyte), Sample(2, "b", 100 * Mebibyte, null)]
                    };

        // Act
        var pss = ProcessSnapshotView.FromProcesses(Snapshot(allPss));
        var rss = ProcessSnapshotView.FromProcesses(Snapshot(someMissing));
        var each = ProcessSnapshotView.FromProcesses(Snapshot(mixed));

        // Assert
        Assert.AreEqual("big-pss", Text(pss.Programs.Rows[0], 0), "programs ordered by PSS");
        Assert.AreEqual(OverviewText.Size(80 * Mebibyte), Text(pss.Programs.Rows[0], 3), "the PSS of the program");
        Assert.AreEqual("2", Text(pss.Processes.Rows[0], 0), "processes ordered by PSS");
        Assert.AreEqual(OverviewText.Size(80 * Mebibyte), Text(pss.Processes.Rows[0], 4), "the PSS of the process");
        Assert.Contains("PSS", pss.Programs.Columns[3], "the program column says PSS");
        Assert.Contains("PSS", pss.Processes.Columns[4], "the process column says PSS");
        Assert.AreEqual("big-rss", Text(rss.Programs.Rows[0], 0), "programs ordered by RSS when one lacks PSS");
        Assert.AreEqual(OverviewText.Size(900 * Mebibyte), Text(rss.Programs.Rows[0], 3), "the RSS of the program");
        Assert.AreEqual("1", Text(rss.Processes.Rows[0], 0), "processes ordered by RSS when one lacks PSS");
        Assert.Contains("RSS", rss.Programs.Columns[3], "the program column says RSS");
        Assert.Contains("RSS", rss.Processes.Columns[4], "the process column says RSS");
        Assert.Contains("PSS", each.Programs.Columns[3], "each list decides for itself: programs with PSS");
        Assert.Contains("RSS", each.Processes.Columns[4], "each list decides for itself: processes without PSS");
    }

    /// <summary>
    /// An incomplete snapshot without a total has a note; with a total or complete it has none.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void FromNotesIncompleteWithoutTotal()
    {
        // Arrange
        var programs = new List<ProgramAggregate>
                       {
                           HistorySamples.Program("nginx", 1, 1, Mebibyte)
                       };

        // Act
        var incomplete = ProcessSnapshotView.FromProcesses(Snapshot(new ProcessSnapshot
                                                                    {
                                                                        Complete = false,
                                                                        Programs = programs
                                                                    }));
        var withTotal = ProcessSnapshotView.FromProcesses(Snapshot(new ProcessSnapshot
                                                                   {
                                                                       Complete = false,
                                                                       Total = 400,
                                                                       Programs = programs
                                                                   }));
        var complete = ProcessSnapshotView.FromProcesses(Snapshot(new ProcessSnapshot
                                                                  {
                                                                      Complete = true,
                                                                      Programs = programs
                                                                  }));

        // Assert
        Assert.AreEqual("This snapshot does not list every process.", incomplete.Note, "the note of an incomplete snapshot");
        Assert.AreEqual(HistoryView.IncompleteProcessesText, incomplete.Note, "the constant has the same text");
        Assert.AreEqual(string.Empty, withTotal.Note, "no note when the total says how many exist");
        Assert.AreEqual(string.Empty, complete.Note, "no note for a complete snapshot");
    }

    /// <summary>
    /// The source, programs, users and commands are external text: escaped, capped at 256 characters and 16 runs.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void FromEscapesSourceCommandAndUser()
    {
        // Arrange
        var control = ((char)0x202E).ToString();
        var source = $"web{control}1";
        var program = $"nginx{control}";
        var user = $"ro{control}ot";
        var command = string.Concat(Enumerable.Repeat($"c{control}", 150));
        var snapshot = new ProcessSnapshot
                       {
                           Complete = true,
                           Programs = [HistorySamples.Program(program, 1, 1, Mebibyte)],
                           Processes = [Process(7, user, command, Mebibyte)]
                       };

        // Act
        var view = ProcessSnapshotView.FromProcesses(HistorySamples.Record(5, source, HistorySamples.Base, snapshot));

        // Assert
        AssertRuns(source, view.Source, "the source");
        AssertRuns(program, view.Programs.Rows[0].Cells[0], "the program");
        AssertRuns(user, view.Processes.Rows[0].Cells[1], "the user");
        AssertRuns(command, view.Processes.Rows[0].Cells[2], "the command, capped");
        Assert.IsLessThanOrEqualTo(HistoryView.TextRuns, view.Processes.Rows[0].Cells[2].Count, "at most 16 runs");
    }

    /// <summary>
    /// A snapshot without programs or processes gives empty lists.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void FromReadsASnapshotWithOnlyOneList()
    {
        // Arrange
        var onlyPrograms = new ProcessSnapshot
                           {
                               Complete = true,
                               Programs = [HistorySamples.Program("nginx", 1, 1, Mebibyte)]
                           };
        var onlyProcesses = new ProcessSnapshot
                            {
                                Complete = true,
                                Processes = [Process(1, "root", "init", Mebibyte)]
                            };

        // Act
        var programs = ProcessSnapshotView.FromProcesses(Snapshot(onlyPrograms));
        var processes = ProcessSnapshotView.FromProcesses(Snapshot(onlyProcesses));

        // Assert
        Assert.HasCount(1, programs.Programs.Rows, "the program");
        Assert.IsEmpty(programs.Processes.Rows, "no processes");
        Assert.AreEqual(string.Empty, programs.Processes.More, "no more processes");
        Assert.IsEmpty(processes.Programs.Rows, "no programs");
        Assert.HasCount(1, processes.Processes.Rows, "the process");
    }

    /// <summary>
    /// Wraps a payload in a stored record.
    /// </summary>
    /// <param name="snapshot">The payload</param>
    /// <returns>The record</returns>
    private static StoredRecord Snapshot(ProcessSnapshot snapshot)
    {
        return HistorySamples.Record(5, "agent", HistorySamples.Base, snapshot);
    }

    /// <summary>
    /// Creates a process.
    /// </summary>
    /// <param name="pid">The process ID</param>
    /// <param name="user">The user</param>
    /// <param name="command">The command</param>
    /// <param name="rss">The resident bytes</param>
    /// <returns>The process</returns>
    private static ProcessSample Process(int pid, string user, string command, ulong rss)
    {
        return new ProcessSample
               {
                   Pid = pid,
                   User = user,
                   Command = command,
                   CpuPercent = 12.5,
                   RssBytes = rss
               };
    }

    /// <summary>
    /// Creates a process with a PSS value.
    /// </summary>
    /// <param name="pid">The process ID</param>
    /// <param name="command">The command</param>
    /// <param name="rss">The resident bytes</param>
    /// <param name="pss">The proportional bytes, or <c>null</c></param>
    /// <returns>The process</returns>
    private static ProcessSample Sample(int pid, string command, ulong rss, ulong? pss)
    {
        var sample = Process(pid, "root", command, rss);

        sample.PssBytes = pss;

        return sample;
    }

    /// <summary>
    /// Creates a program with a PSS value.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="rss">The resident bytes</param>
    /// <param name="pss">The proportional bytes, or <c>null</c></param>
    /// <returns>The program</returns>
    private static ProgramAggregate Aggregate(string program, ulong rss, ulong? pss)
    {
        var aggregate = HistorySamples.Program(program, 1, 1, rss);

        aggregate.PssBytes = pss;

        return aggregate;
    }

    /// <summary>
    /// Joins the text of the cells of a row with a bar.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The text</returns>
    private static string Cells(SnapshotRowView row)
    {
        return string.Join('|', row.Cells.Select(HistorySamples.Text));
    }

    /// <summary>
    /// Gets the text of a cell.
    /// </summary>
    /// <param name="row">The row</param>
    /// <param name="index">The index of the cell</param>
    /// <returns>The text</returns>
    private static string Text(SnapshotRowView row, int index)
    {
        return HistorySamples.Text(row.Cells[index]);
    }

    /// <summary>
    /// Checks that runs are those of the display of a text.
    /// </summary>
    /// <param name="text">The external text</param>
    /// <param name="runs">The runs</param>
    /// <param name="what">What the runs are</param>
    private static void AssertRuns(string text, IReadOnlyList<TextRun> runs, string what)
    {
        var expected = LogText.Runs(text, HistoryView.TextChars, HistoryView.TextRuns);

        Assert.AreEqual(string.Join('|', expected.Select(run => $"{run.Escaped}:{run.Text}")), string.Join('|', runs.Select(run => $"{run.Escaped}:{run.Text}")), $"{what}: the runs of the display");
        Assert.DoesNotContain("‮", string.Concat(runs.Where(run => run.Escaped == false).Select(run => run.Text)), $"{what}: the control character is only escaped");
    }

    #endregion // Methods
}