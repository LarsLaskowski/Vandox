using Vandox.Backend.Network;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="NetworkListenerView"/>
/// </summary>
[TestClass]
public class NetworkListenerViewTests
{
    #region Constants

    private const string NoComparisonText = "No connection snapshot of this source is stored from 24 to 48 hours before, so no listener is marked new.";
    private const string NotComparableText = "No listener is marked new: the inventories are not comparable.";
    private const string PrivilegedIncompleteText = "No listener is marked new: ports 1 to 1023 are not complete.";
    private const string NotComparedText = "Listeners of imported or older snapshots are not compared.";
    private const string PrivilegedListText = "This snapshot lists only the listeners on ports 1 to 1023.";
    private const string PrivilegedOnlyEnd = "(ports 1 to 1023 only)";
    private const char RightToLeftOverride = (char)0x202E;

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _shown = HistorySamples.Base;

    private static readonly DateTimeOffset _earlier = HistorySamples.Base.AddHours(-30);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// A listener the comparison snapshot lacks is marked new and listed first, the rest follows in snapshot order, and the summary counts the new ones and names the time of the comparison.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void NetworkListenerViewBuildMarksNewFirst()
    {
        // Arrange
        var shown = Shown("all", NetworkSamples.Tcp(22), NetworkSamples.Listener("tcp", "0.0.0.0:8443", 41, "app"), NetworkSamples.Tcp(80), NetworkSamples.Tcp(9000));
        var earlier = Earlier("all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80));

        // Act
        var view = NetworkListenerView.Build(shown, earlier);

        // Assert
        Assert.AreSequenceEqual(["new|tcp|0.0.0.0:8443|41 app|unknown", "new|tcp|0.0.0.0:9000|no owner|unknown", "|tcp|0.0.0.0:22|no owner|unknown", "|tcp|0.0.0.0:80|no owner|unknown"], view.Table.Rows.Select(NetworkSamples.Cells).ToArray(), "new listeners first, then the rest, each in snapshot order");
        Assert.AreEqual("2 new listeners since 2026-09-30 04:00:00 UTC.", view.Summary, "the summary names the time of the comparison");
        Assert.IsEmpty(view.PanelNotes, "no note");
        Assert.AreEqual(_shown, view.CapturedAt, "the capture time of the shown snapshot");
        Assert.AreEqual("agent", HistorySamples.Text(view.SourceText), "the source of the snapshot");
        Assert.AreEqual(string.Empty, view.Text, "no text instead of the panel");
        Assert.AreSequenceEqual(["State", "Protocol", "Endpoint", "Owner", "Socket UID"], view.Table.Columns.ToArray(), "the columns");
    }

    /// <summary>
    /// One new listener is told in the singular, none as no new listener.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void NetworkListenerViewBuildSummaryTellsOneAndNone()
    {
        // Arrange
        var earlier = Earlier("all", NetworkSamples.Tcp(22));

        // Act
        var one = NetworkListenerView.Build(Shown("all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80)), earlier);
        var none = NetworkListenerView.Build(Shown("all", NetworkSamples.Tcp(22)), earlier);

        // Assert
        Assert.AreEqual("1 new listener since 2026-09-30 04:00:00 UTC.", one.Summary, "one new listener");
        Assert.AreEqual("No new listener since 2026-09-30 04:00:00 UTC.", none.Summary, "no new listener");
        Assert.AreEqual("|tcp|0.0.0.0:22|no owner|unknown", NetworkSamples.Cells(none.Table.Rows[0]), "an unchanged listener is not marked");
    }

    /// <summary>
    /// The owner and the socket UID of each listener are shown as the spec says them.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void NetworkListenerViewBuildShowsOwnerAndSocketUid()
    {
        // Arrange
        var shown = Shown("all",
                          NetworkSamples.Listener("tcp", "0.0.0.0:22", 41, "sshd", 0, "root"),
                          NetworkSamples.Listener("tcp6", "[::]:8443", 42, "app", 1000),
                          NetworkSamples.Listener("udp", "127.0.0.1:53", 0, string.Empty, null),
                          NetworkSamples.Listener("tcp", "0.0.0.0:80", 43, "nginx", 33, "www-data", true));

        // Act
        var view = NetworkListenerView.Build(shown, Earlier("all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80), NetworkSamples.Listener("tcp6", "[::]:8443"), NetworkSamples.Listener("udp", "127.0.0.1:53")));

        // Assert
        Assert.AreSequenceEqual(["|tcp|0.0.0.0:22|41 sshd|0 root", "|tcp6|[::]:8443|42 app|1000", "|udp|127.0.0.1:53|no owner|unknown", "|tcp|0.0.0.0:80|43 nginx|33 www-data (lowest of several)"], view.Table.Rows.Select(NetworkSamples.Cells).ToArray(), "owner and socket UID cells");
    }

    /// <summary>
    /// Without a comparison snapshot the listeners are shown unmarked and the summary line says that none is marked new.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void NetworkListenerViewBuildNoComparisonSnapshot()
    {
        // Arrange
        var shown = Shown("all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80));

        // Act
        var view = NetworkListenerView.Build(shown, null);

        // Assert
        Assert.AreEqual(NoComparisonText, view.Summary, "the line of E7");
        Assert.IsEmpty(view.PanelNotes, "no further note");
        Assert.AreSequenceEqual(["|tcp|0.0.0.0:22|no owner|unknown", "|tcp|0.0.0.0:80|no owner|unknown"], view.Table.Rows.Select(NetworkSamples.Cells).ToArray(), "the listeners in snapshot order, none marked");
    }

    /// <summary>
    /// Inventories that cannot be compared mark nothing and say why, in the notes and not in the summary.
    /// </summary>
    /// <param name="shownInventory">The inventory of the shown snapshot</param>
    /// <param name="earlierInventory">The inventory of the comparison snapshot</param>
    /// <param name="expected">The note</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow("all", "skipped", NotComparableText)]
    [DataRow("skipped", "all", NotComparableText)]
    [DataRow("all", "cut", PrivilegedIncompleteText)]
    [DataRow("cut", "all", PrivilegedIncompleteText)]
    public void NetworkListenerViewBuildNotComparable(string shownInventory, string earlierInventory, string expected)
    {
        // Arrange
        var shown = Shown(shownInventory, NetworkSamples.Tcp(22), NetworkSamples.Tcp(80));
        var earlier = Earlier(earlierInventory, NetworkSamples.Tcp(22));

        // Act
        var view = NetworkListenerView.Build(shown, earlier);

        // Assert
        Assert.AreSequenceEqual([expected], view.PanelNotes.ToArray(), "the note of E8");
        Assert.AreEqual(string.Empty, view.Summary, "no summary of new listeners");
        Assert.IsTrue(view.Table.Rows.All(row => NetworkSamples.Cells(row).StartsWith('|')), "nothing is marked");
        Assert.HasCount(2, view.Table.Rows, "the listeners are shown");
    }

    /// <summary>
    /// When either inventory holds only the privileged ports only those are compared and the summary ends with the ports it covers.
    /// </summary>
    /// <param name="shownInventory">The inventory of the shown snapshot</param>
    /// <param name="earlierInventory">The inventory of the comparison snapshot</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow("privileged", "all")]
    [DataRow("all", "privileged")]
    [DataRow("privileged", "privileged")]
    public void NetworkListenerViewBuildPrivilegedOnly(string shownInventory, string earlierInventory)
    {
        // Arrange
        var shown = Shown(shownInventory, NetworkSamples.Tcp(22), NetworkSamples.Tcp(25), NetworkSamples.Tcp(8443));
        var earlier = Earlier(earlierInventory, NetworkSamples.Tcp(22));

        // Act
        var view = NetworkListenerView.Build(shown, earlier);

        // Assert
        Assert.StartsWith("1 new listener since 2026-09-30 04:00:00 UTC", view.Summary, "only the privileged port is new");
        Assert.Contains(PrivilegedOnlyEnd, view.Summary, "the summary names the ports it covers");
        Assert.AreEqual("new|tcp|0.0.0.0:25|no owner|unknown", NetworkSamples.Cells(view.Table.Rows[0]), "port 25 is new");
        Assert.IsTrue(view.Table.Rows.Skip(1).All(row => NetworkSamples.Cells(row).StartsWith('|')), "the other ports are not marked");
    }

    /// <summary>
    /// A shown snapshot that lists only the privileged ports says so in a note of its own.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void NetworkListenerViewBuildNotesPrivilegedList()
    {
        // Arrange
        var privileged = Shown("privileged", NetworkSamples.Tcp(22));
        var whole = Shown("all", NetworkSamples.Tcp(22));

        // Act
        var onlyPrivileged = NetworkListenerView.Build(privileged, Earlier("all", NetworkSamples.Tcp(22)));
        var all = NetworkListenerView.Build(whole, Earlier("privileged", NetworkSamples.Tcp(22)));

        // Assert
        Assert.AreSequenceEqual([PrivilegedListText], onlyPrivileged.PanelNotes.ToArray(), "the note of E10");
        Assert.IsEmpty(all.PanelNotes, "a whole shown list has no such note, whatever the comparison covers");
    }

    /// <summary>
    /// A snapshot without an inventory (an import or a 1.0 agent), shown or compared, shows its listeners unmarked with the note of E9 alone: no line for a missing comparison and no outcome of the comparer.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void NetworkListenerViewBuildWithoutInventory()
    {
        // Arrange
        var shownWithout = Shown(null, NetworkSamples.Tcp(22), NetworkSamples.Tcp(80));
        var shownWith = Shown("all", NetworkSamples.Tcp(22), NetworkSamples.Tcp(80));
        var privilegedShown = Shown("privileged", NetworkSamples.Tcp(22));

        // Act
        var shownNoEarlier = NetworkListenerView.Build(shownWithout, null);
        var shownWithEarlier = NetworkListenerView.Build(shownWithout, Earlier("all", NetworkSamples.Tcp(22)));
        var comparisonWithout = NetworkListenerView.Build(shownWith, Earlier(null, NetworkSamples.Tcp(22)));
        var both = NetworkListenerView.Build(privilegedShown, Earlier(null, NetworkSamples.Tcp(22)));

        // Assert
        Assert.AreSequenceEqual([NotComparedText], shownNoEarlier.PanelNotes.ToArray(), "shown without inventory and no comparison: E9 alone");
        Assert.AreEqual(string.Empty, shownNoEarlier.Summary, "no line of E7 for a shown snapshot without inventory");
        Assert.AreSequenceEqual([NotComparedText], shownWithEarlier.PanelNotes.ToArray(), "shown without inventory: E9");
        Assert.AreEqual(string.Empty, shownWithEarlier.Summary, "no summary");
        Assert.AreSequenceEqual([NotComparedText], comparisonWithout.PanelNotes.ToArray(), "comparison without inventory: E9 and not E8");
        Assert.AreEqual(string.Empty, comparisonWithout.Summary, "no summary for a comparison without inventory");
        Assert.AreSequenceEqual([NotComparedText, PrivilegedListText], both.PanelNotes.ToArray(), "the notes in the order E9, E10");
        Assert.AreSequenceEqual(["|tcp|0.0.0.0:22|no owner|unknown", "|tcp|0.0.0.0:80|no owner|unknown"], shownNoEarlier.Table.Rows.Select(NetworkSamples.Cells).ToArray(), "the listeners are shown, none marked");
        Assert.AreSequenceEqual(["|tcp|0.0.0.0:22|no owner|unknown", "|tcp|0.0.0.0:80|no owner|unknown"], comparisonWithout.Table.Rows.Select(NetworkSamples.Cells).ToArray(), "the listeners are shown, none marked, with a comparison without inventory");
    }

    /// <summary>
    /// At most 200 rows are shown and the rest is counted; every new listener is marked and listed first, also beyond the comparer's limit of 64.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void NetworkListenerViewBuildListsRowsWithCap()
    {
        // Arrange
        var old = Enumerable.Range(0, 140).Select(index => NetworkSamples.Tcp(2000 + index)).ToArray();
        var added = Enumerable.Range(0, 70).Select(index => NetworkSamples.Tcp(3000 + index)).ToArray();
        var exact = Enumerable.Range(0, NetworkView.ListenerRows).Select(index => NetworkSamples.Tcp(2000 + index)).ToArray();
        var capped = Enumerable.Range(0, NetworkView.ListenerRows + 5).Select(index => NetworkSamples.Tcp(2000 + index)).ToArray();

        // Act
        var many = NetworkListenerView.Build(Shown("all", [.. old, .. added]), Earlier("all", old));
        var twoHundred = NetworkListenerView.Build(Shown("all", exact), Earlier("all", exact));
        var cut = NetworkListenerView.Build(Shown("all", capped), Earlier("all", capped));

        // Assert
        Assert.HasCount(NetworkView.ListenerRows, many.Table.Rows, "200 rows");
        Assert.IsTrue(many.Table.Rows.Take(70).All(row => NetworkSamples.Cells(row).StartsWith("new|", StringComparison.Ordinal)), "the 70 new listeners come first and are all marked");
        Assert.IsTrue(many.Table.Rows.Skip(70).All(row => NetworkSamples.Cells(row).StartsWith('|')), "the rest is not marked");
        Assert.AreEqual("70 new listeners since 2026-09-30 04:00:00 UTC.", many.Summary, "every new listener is counted");
        Assert.AreEqual("and 10 more listeners", many.Table.More, "the rows beyond 200");
        Assert.AreEqual(string.Empty, twoHundred.Table.More, "exactly 200 rows");
        Assert.AreEqual("and 5 more listeners", cut.Table.More, "five beyond the cap");
    }

    /// <summary>
    /// The source and the listener texts are external text: escaped, with a run marked escaped.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void NetworkListenerViewBuildEscapesExternalText()
    {
        // Arrange
        var record = NetworkSamples.Connections(1, $"web{RightToLeftOverride}1", _shown, "all", NetworkSamples.Listener("tcp", "0.0.0.0:22", 41, $"c{RightToLeftOverride}md", 1000, $"us{RightToLeftOverride}er"));

        // Act
        var view = NetworkListenerView.Build(record, null);

        // Assert
        Assert.AreEqual("web\\u202e1", HistorySamples.Text(view.SourceText), "the source is written with its escape");
        Assert.IsTrue(NetworkSamples.AnyEscaped(view.SourceText), "a source run is escaped");
        Assert.IsTrue(NetworkSamples.AnyEscaped(view.Table.Rows[0].Cells[3]), "the command is escaped");
        Assert.IsTrue(NetworkSamples.AnyEscaped(view.Table.Rows[0].Cells[4]), "the user name is escaped");
        Assert.DoesNotContain(RightToLeftOverride.ToString(), NetworkSamples.Cells(view.Table.Rows[0]), "the raw character is gone");
    }

    /// <summary>
    /// The empty view carries only its text, and a build without a shown snapshot is refused.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void NetworkListenerViewEmptyCarriesOnlyItsText()
    {
        // Act
        var view = NetworkListenerView.Empty("No connection snapshot of this source is stored in this range.");

        // Assert
        Assert.AreEqual("No connection snapshot of this source is stored in this range.", view.Text, "the text");
        Assert.IsNull(view.CapturedAt, "no capture time");
        Assert.IsEmpty(view.Table.Rows, "no rows");
        Assert.AreEqual(string.Empty, view.Summary, "no summary");
        Assert.IsEmpty(view.PanelNotes, "no notes");
        Assert.Throws<ArgumentNullException>(() => NetworkListenerView.Build(null!, null), "no shown snapshot");
    }

    /// <summary>
    /// A record that is not a connection snapshot is refused, naming the shown parameter.
    /// </summary>
    [TestMethod]
    public void NetworkListenerViewBuildRefusesOtherRecords()
    {
        // Arrange
        var other = HistorySamples.Record(2, "agent", _shown, new Gap());

        // Act
        var thrown = Assert.ThrowsExactly<ArgumentException>(() => NetworkListenerView.Build(other, null), "no connection snapshot");

        // Assert
        Assert.AreEqual("shown", thrown.ParamName, "the parameter");
    }

    /// <summary>
    /// A snapshot without a listener list gives an empty table, and a listener without an endpoint an empty endpoint cell.
    /// </summary>
    [TestMethod]
    public void NetworkListenerViewBuildToleratesMissingListenersAndEndpoint()
    {
        // Arrange
        var without = HistorySamples.Record(2,
                                            "agent",
                                            _shown,
                                            new ConnectionSnapshot
                                            {
                                                Complete = true,
                                                Inventory = "all",
                                                Listeners = null
                                            });
        var noEndpoint = Shown("all",
                               new Listener
                               {
                                   Proto = "tcp",
                                   Local = null
                               });

        // Act
        var empty = NetworkListenerView.Build(without, null);
        var row = NetworkListenerView.Build(noEndpoint, null).Table.Rows[0];

        // Assert
        Assert.IsEmpty(empty.Table.Rows, "no rows");
        Assert.AreEqual(string.Empty, empty.Table.More, "no more line");
        Assert.AreEqual("|tcp||no owner|unknown", NetworkSamples.Cells(row), "the endpoint cell is empty");
    }

    /// <summary>
    /// Creates the shown snapshot of the agent.
    /// </summary>
    /// <param name="inventory">The inventory state</param>
    /// <param name="listeners">The listeners</param>
    /// <returns>The record</returns>
    private static StoredRecord Shown(string? inventory, params Listener[] listeners)
    {
        return NetworkSamples.Connections(2, "agent", _shown, inventory, listeners);
    }

    /// <summary>
    /// Creates the comparison snapshot of the agent, 30 hours before the shown one.
    /// </summary>
    /// <param name="inventory">The inventory state</param>
    /// <param name="listeners">The listeners</param>
    /// <returns>The record</returns>
    private static StoredRecord Earlier(string? inventory, params Listener[] listeners)
    {
        return NetworkSamples.Connections(1, "agent", _earlier, inventory, listeners);
    }

    #endregion // Methods
}