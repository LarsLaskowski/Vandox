using System.Net;

using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ConnectionSnapshotView"/>
/// </summary>
[TestClass]
public class ConnectionSnapshotViewTests
{
    #region Methods

    /// <summary>
    /// The counts of every protocol and state are listed, and the processes and remote addresses with the most connections come first.
    /// </summary>
    [TestMethod]
    [TestCategory("AC10")]
    public void FromListsStatesTopProcessesAndRemotes()
    {
        // Arrange
        var snapshot = new ConnectionSnapshot
                       {
                           Complete = true,
                           States = [HistorySamples.State("tcp", "ESTABLISHED", 120), HistorySamples.State("tcp", "TIME_WAIT", 7), HistorySamples.State("udp", "UNCONN", 3)],
                           Processes = [Connections(10, "sshd", 5), Connections(30, "nginx", 90), Connections(20, "mysqld", 90), Connections(40, "cron", 1)],
                           Remotes = [Remote("203.0.113.9", 4), Remote("203.0.113.7", 40), Remote("198.51.100.2", 12)]
                       };

        // Act
        var view = ConnectionSnapshotView.FromConnections(HistorySamples.Record(5, "agent", HistorySamples.Base, snapshot));

        // Assert
        Assert.AreEqual(HistorySamples.Base, view.CapturedAt, "the capture time");
        Assert.AreEqual("tcp|ESTABLISHED|120;tcp|TIME_WAIT|7;udp|UNCONN|3", string.Join(';', view.States.Rows.Select(Cells).Order(StringComparer.Ordinal)), "every protocol and state with its count");
        Assert.AreEqual(string.Empty, view.States.More, "the states are not capped");
        Assert.AreEqual("20|mysqld|90;30|nginx|90;10|sshd|5;40|cron|1", string.Join(';', view.Processes.Rows.Select(Cells)), "processes by connections, a tie by process ID");
        Assert.AreEqual("203.0.113.7|40;198.51.100.2|12;203.0.113.9|4", string.Join(';', view.Remotes.Rows.Select(Cells)), "remote addresses by connections");
        Assert.AreEqual(string.Empty, view.Processes.More, "no more processes");
        Assert.AreEqual(string.Empty, view.Remotes.More, "no more remote addresses");
    }

    /// <summary>
    /// The remote addresses of a counts-only snapshot are headed as the top 25 the snapshot keeps; those of another snapshot are not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC10")]
    public void FromHeadsTheRemotesOfACountsOnlySnapshot()
    {
        // Arrange
        var countsOnly = new ConnectionSnapshot
                         {
                             Complete = true,
                             AggregatesOnly = true,
                             Remotes = [Remote("203.0.113.7", 4)]
                         };
        var full = new ConnectionSnapshot
                   {
                       Complete = true,
                       Remotes = [Remote("203.0.113.7", 4)]
                   };

        // Act
        var counts = ConnectionSnapshotView.FromConnections(Snapshot(countsOnly));
        var other = ConnectionSnapshotView.FromConnections(Snapshot(full));

        // Assert
        Assert.AreEqual("Top remote addresses (the snapshot keeps only the 25 most frequent)", counts.Remotes.Heading, "the heading of a counts-only snapshot");
        Assert.AreNotEqual(counts.Remotes.Heading, other.Remotes.Heading, "another snapshot has another heading");
    }

    /// <summary>
    /// Incomplete counts have a note; others have none.
    /// </summary>
    [TestMethod]
    [TestCategory("AC10")]
    public void FromNotesIncompleteCounts()
    {
        // Arrange
        var incomplete = new ConnectionSnapshot
                         {
                             Complete = true,
                             AggregatesOnly = true,
                             AggregatesIncomplete = true,
                             States = [HistorySamples.State("tcp", "ESTABLISHED", 1)]
                         };
        var partial = new ConnectionSnapshot
                      {
                          Complete = false,
                          AggregatesOnly = true,
                          States = [HistorySamples.State("tcp", "ESTABLISHED", 1)]
                      };
        var complete = new ConnectionSnapshot
                       {
                           Complete = true,
                           States = [HistorySamples.State("tcp", "ESTABLISHED", 1)]
                       };

        // Act
        var noted = ConnectionSnapshotView.FromConnections(Snapshot(incomplete));
        var notNoted = ConnectionSnapshotView.FromConnections(Snapshot(partial));
        var none = ConnectionSnapshotView.FromConnections(Snapshot(complete));

        // Assert
        Assert.AreEqual("These counts may not cover every socket.", noted.Note, "the note of incomplete counts");
        Assert.AreEqual(HistoryView.IncompleteConnectionsText, noted.Note, "the constant has the same text");
        Assert.AreEqual(string.Empty, notNoted.Note, "a snapshot partial by design has no note");
        Assert.AreEqual(string.Empty, none.Note, "a complete snapshot has no note");
    }

    /// <summary>
    /// Beyond 20 processes or remote addresses the rest is counted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC10")]
    public void FromCountsMore()
    {
        // Arrange
        var snapshot = new ConnectionSnapshot
                       {
                           Complete = true,
                           Processes = [.. Enumerable.Range(1, 23).Select(index => Connections(index, $"cmd{index}", (uint)index))],
                           Remotes = [.. Enumerable.Range(1, 21).Select(index => Remote($"203.0.113.{index}", (uint)index))]
                       };
        var exact = new ConnectionSnapshot
                    {
                        Complete = true,
                        Processes = [.. Enumerable.Range(1, 20).Select(index => Connections(index, $"cmd{index}", (uint)index))],
                        Remotes = [.. Enumerable.Range(1, 20).Select(index => Remote($"203.0.113.{index}", (uint)index))]
                    };

        // Act
        var view = ConnectionSnapshotView.FromConnections(Snapshot(snapshot));
        var twenty = ConnectionSnapshotView.FromConnections(Snapshot(exact));

        // Assert
        Assert.HasCount(20, view.Processes.Rows, "20 processes");
        Assert.AreEqual("23|cmd23|23", Cells(view.Processes.Rows[0]), "the process with the most connections first");
        Assert.AreEqual("and 3 more", view.Processes.More, "the processes beyond 20");
        Assert.HasCount(20, view.Remotes.Rows, "20 remote addresses");
        Assert.AreEqual("and 1 more", view.Remotes.More, "the remote addresses beyond 20");
        Assert.AreEqual(string.Empty, twenty.Processes.More, "exactly 20 processes");
        Assert.AreEqual(string.Empty, twenty.Remotes.More, "exactly 20 remote addresses");
    }

    /// <summary>
    /// The source, commands and addresses are external text: escaped, capped at 256 characters and 16 runs.
    /// </summary>
    [TestMethod]
    [TestCategory("AC10")]
    public void FromEscapesSource()
    {
        // Arrange
        var control = ((char)0x202E).ToString();
        var source = $"web{control}1";
        var command = string.Concat(Enumerable.Repeat($"c{control}", 150));
        var snapshot = new ConnectionSnapshot
                       {
                           Complete = true,
                           Processes = [Connections(7, command, 3)],
                           Remotes = [Remote("203.0.113.7", 3)]
                       };

        // Act
        var view = ConnectionSnapshotView.FromConnections(HistorySamples.Record(5, source, HistorySamples.Base, snapshot));

        // Assert
        AssertRuns(source, view.Source, "the source");
        AssertRuns(command, view.Processes.Rows[0].Cells[1], "the command");
        Assert.IsLessThanOrEqualTo(HistoryView.TextRuns, view.Processes.Rows[0].Cells[1].Count, "at most 16 runs");
        Assert.AreEqual("203.0.113.7", HistorySamples.Text(view.Remotes.Rows[0].Cells[0]), "the address");
    }

    /// <summary>
    /// Wraps a payload in a stored record.
    /// </summary>
    /// <param name="snapshot">The payload</param>
    /// <returns>The record</returns>
    private static StoredRecord Snapshot(ConnectionSnapshot snapshot)
    {
        return HistorySamples.Record(5, "agent", HistorySamples.Base, snapshot);
    }

    /// <summary>
    /// Creates the connections of a process.
    /// </summary>
    /// <param name="pid">The process ID</param>
    /// <param name="command">The command</param>
    /// <param name="count">The number of connections</param>
    /// <returns>The entry</returns>
    private static ProcessConnections Connections(int pid, string command, uint count)
    {
        return new ProcessConnections
               {
                   Pid = pid,
                   Command = command,
                   Count = count
               };
    }

    /// <summary>
    /// Creates the count of a remote address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="count">The number of connections</param>
    /// <returns>The entry</returns>
    private static RemoteCount Remote(string address, uint count)
    {
        return new RemoteCount
               {
                   Addr = IPAddress.Parse(address),
                   Count = count
               };
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