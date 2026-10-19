using System.Net;

using Vandox.Core.Model;
using Vandox.Core.Reports;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ListenerComparer"/>
/// </summary>
[TestClass]
public class ListenerComparerTests
{
    #region Methods

    /// <summary>
    /// Whole inventories are compared on every port and list the new listeners of any port.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareWholeInventoriesComparesAllPorts()
    {
        // Arrange
        var earlier = Snapshot("all", Tcp("0.0.0.0:22"));
        var later = Snapshot("all", Tcp("0.0.0.0:22"), Listen("tcp", "0.0.0.0:23", 41, "telnetd"), Listen("tcp6", "[::]:8443", 42, "app"));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.AllPorts, result.Outcome, "outcome");
        Assert.AreSequenceEqual(["tcp 0.0.0.0:23", "tcp6 [::]:8443"], Describe(result.NewListeners), "new listeners");
        Assert.AreEqual(0L, result.Omitted, "omitted");
    }

    /// <summary>
    /// A cut inventory on either side compares ports 1 to 1023 only.
    /// </summary>
    /// <param name="earlierInventory">The inventory state of the earlier snapshot</param>
    /// <param name="laterInventory">The inventory state of the later snapshot</param>
    [TestMethod]
    [DataRow("privileged", "all")]
    [DataRow("all", "privileged")]
    [DataRow("privileged", "privileged")]
    public void ListenerComparerCompareCutInventoryComparesPrivilegedOnly(string earlierInventory, string laterInventory)
    {
        // Arrange
        var earlier = Snapshot(earlierInventory, Tcp("0.0.0.0:22"));
        var later = Snapshot(laterInventory, Tcp("0.0.0.0:22"), Tcp("0.0.0.0:25"), Tcp("0.0.0.0:8443"));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.PrivilegedOnly, result.Outcome, "outcome");
        Assert.AreSequenceEqual(["tcp 0.0.0.0:25"], Describe(result.NewListeners), "only the privileged port is new");
    }

    /// <summary>
    /// Without an earlier snapshot nothing is compared.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareWithoutBaseIsNoEarlierInventory()
    {
        // Arrange
        var later = Snapshot("all", Tcp("0.0.0.0:23"));

        // Act
        var result = ListenerComparer.Compare(null, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.NoEarlierInventory, result.Outcome, "outcome");
        Assert.IsEmpty(result.NewListeners, "no listener is listed");
        Assert.AreEqual(0L, result.Omitted, "omitted");
    }

    /// <summary>
    /// A skipped line on either side, or different tables, make the pair not comparable.
    /// </summary>
    /// <param name="earlierInventory">The inventory state of the earlier snapshot</param>
    /// <param name="laterInventory">The inventory state of the later snapshot</param>
    /// <param name="earlierTables">The tables of the earlier snapshot, comma separated</param>
    /// <param name="laterTables">The tables of the later snapshot, comma separated</param>
    [TestMethod]
    [DataRow("skipped", "all", "tcp,tcp6", "tcp,tcp6")]
    [DataRow("all", "skipped", "tcp,tcp6", "tcp,tcp6")]
    [DataRow("all", "all", "tcp,tcp6,udp,udp6", "tcp,tcp6")]
    [DataRow("all", "all", "tcp", "tcp,tcp6")]
    public void ListenerComparerCompareSkippedOrOtherTablesIsNotComparable(string earlierInventory,
                                                                           string laterInventory,
                                                                           string earlierTables,
                                                                           string laterTables)
    {
        // Arrange
        var earlier = Snapshot(earlierInventory, Tcp("0.0.0.0:22"));
        var later = Snapshot(laterInventory, Tcp("0.0.0.0:22"), Tcp("0.0.0.0:23"));
        earlier.Tables = [.. earlierTables.Split(',')];
        later.Tables = [.. laterTables.Split(',')];

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.NotComparable, result.Outcome, "outcome");
        Assert.IsEmpty(result.NewListeners, "no listener is listed");
    }

    /// <summary>
    /// A cut inside the privileged part on either side gives the privileged incomplete outcome, and a skipped line wins over it.
    /// </summary>
    /// <param name="earlierInventory">The inventory state of the earlier snapshot</param>
    /// <param name="laterInventory">The inventory state of the later snapshot</param>
    /// <param name="expected">The expected outcome</param>
    [TestMethod]
    [DataRow("cut", "all", ListenerOutcome.PrivilegedIncomplete)]
    [DataRow("all", "cut", ListenerOutcome.PrivilegedIncomplete)]
    [DataRow("cut", "privileged", ListenerOutcome.PrivilegedIncomplete)]
    [DataRow("cut", "skipped", ListenerOutcome.NotComparable)]
    [DataRow("skipped", "cut", ListenerOutcome.NotComparable)]
    public void ListenerComparerCompareCutPrivilegedPartIsPrivilegedIncomplete(string earlierInventory,
                                                                               string laterInventory,
                                                                               ListenerOutcome expected)
    {
        // Arrange
        var earlier = Snapshot(earlierInventory, Tcp("0.0.0.0:22"));
        var later = Snapshot(laterInventory, Tcp("0.0.0.0:22"), Tcp("0.0.0.0:23"));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(expected, result.Outcome, "outcome");
        Assert.IsEmpty(result.NewListeners, "no listener is listed");
    }

    /// <summary>
    /// A listener whose owner changed is not new.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareOwnerChangeIsNotNew()
    {
        // Arrange
        var earlier = Snapshot("all", Listen("tcp", "0.0.0.0:80", 10, "apache2"));
        var later = Snapshot("all", Listen("tcp", "0.0.0.0:80", 99, "nginx"));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.AllPorts, result.Outcome, "outcome");
        Assert.IsEmpty(result.NewListeners, "an owner change is not new");
    }

    /// <summary>
    /// An unconnected UDP socket in the kernel's local port range is not listed, other listeners at the edges are.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareEphemeralUdpNotReported()
    {
        // Arrange
        var earlier = Snapshot("all");
        var later = Snapshot("all",
                             Tcp("0.0.0.0:40000"),
                             Listen("udp", "0.0.0.0:32767", 1, "a"),
                             Listen("udp", "0.0.0.0:32768", 2, "b"),
                             Listen("udp", "0.0.0.0:60999", 3, "c"),
                             Listen("udp", "0.0.0.0:61000", 4, "d"),
                             Listen("udp6", "[::]:32767", 5, "e"),
                             Listen("udp6", "[::]:32768", 6, "f"),
                             Listen("udp6", "[::]:60999", 7, "g"),
                             Listen("udp6", "[::]:61000", 8, "h"));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreSequenceEqual(["tcp 0.0.0.0:40000", "udp 0.0.0.0:32767", "udp 0.0.0.0:61000", "udp6 [::]:32767", "udp6 [::]:61000"],
                                Describe(result.NewListeners),
                                "listeners");
    }

    /// <summary>
    /// An IPv4-mapped address is another listener than the plain IPv4 address.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareMappedAddressIsAnotherListener()
    {
        // Arrange
        var earlier = Snapshot("all", Tcp("0.0.0.0:23"));
        var later = Snapshot("all", Tcp("0.0.0.0:23"), Listen("tcp", "[::ffff:0.0.0.0]:23", 5, "mapped"));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.HasCount(1, result.NewListeners, "count");
        Assert.AreEqual(IPEndPoint.Parse("[::ffff:0.0.0.0]:23"), result.NewListeners[0].Local, "the mapped endpoint is new");
        Assert.AreEqual("mapped", result.NewListeners[0].Command, "command");
    }

    /// <summary>
    /// At most 64 new listeners are listed in inventory order, the rest is counted, and the owner comes from the later snapshot.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareListsAtMost64InInventoryOrder()
    {
        // Arrange
        var earlier = Snapshot("all");
        var listeners = new List<Listener>();

        for (var index = 0; index < 70; index++)
        {
            listeners.Add(index == 0 ? Tcp($"0.0.0.0:{10 + index}") : Listen("tcp", $"0.0.0.0:{10 + index}", 100 + index, $"cmd{index}"));
        }

        var later = Snapshot("all", [.. listeners]);

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.AllPorts, result.Outcome, "outcome");
        Assert.HasCount(ListenerComparer.MaxNewListeners, result.NewListeners, "listed");
        Assert.AreEqual(6L, result.Omitted, "omitted");
        Assert.AreSequenceEqual(Describe(listeners.Take(64)), Describe(result.NewListeners), "the first 64 in inventory order");
        Assert.AreEqual(0, result.NewListeners[0].Pid, "no PID when the entry has none");
        Assert.AreEqual(string.Empty, result.NewListeners[0].Command, "no command when the entry has none");
        Assert.AreEqual(163, result.NewListeners[63].Pid, "PID of the later snapshot");
        Assert.AreEqual("cmd63", result.NewListeners[63].Command, "command of the later snapshot");
    }

    #endregion // Methods

    #region Helpers

    /// <summary>
    /// Creates a TCP listener without an owner.
    /// </summary>
    /// <param name="local">The local endpoint</param>
    /// <returns>The listener</returns>
    private static Listener Tcp(string local)
    {
        return Listen("tcp", local, 0, string.Empty);
    }

    /// <summary>
    /// Creates a listener.
    /// </summary>
    /// <param name="proto">The protocol</param>
    /// <param name="local">The local endpoint</param>
    /// <param name="pid">The owner's PID</param>
    /// <param name="command">The owner's command</param>
    /// <returns>The listener</returns>
    private static Listener Listen(string proto, string local, int pid, string command)
    {
        return new Listener
               {
                   Proto = proto,
                   Local = IPEndPoint.Parse(local),
                   Pid = pid,
                   Command = command
               };
    }

    /// <summary>
    /// Creates a snapshot of all four tables.
    /// </summary>
    /// <param name="inventory">The inventory state</param>
    /// <param name="listeners">The listeners</param>
    /// <returns>The snapshot</returns>
    private static ConnectionSnapshot Snapshot(string inventory, params Listener[] listeners)
    {
        return new ConnectionSnapshot
               {
                   Inventory = inventory,
                   Tables = ["tcp", "tcp6", "udp", "udp6"],
                   Listeners = [.. listeners],
                   ListenerCheck = "start"
               };
    }

    /// <summary>
    /// Describes listeners by protocol and endpoint.
    /// </summary>
    /// <param name="listeners">The listeners</param>
    /// <returns>The descriptions</returns>
    private static string[] Describe(IEnumerable<Listener> listeners)
    {
        return [.. listeners.Select(listener => $"{listener.Proto} {listener.Local}")];
    }

    #endregion // Helpers
}