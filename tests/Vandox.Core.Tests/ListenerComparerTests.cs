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

    /// <summary>
    /// A known endpoint whose lowest socket UID differs is reported as changed with the owner of the later snapshot and the UID and flag of the earlier one, and is not new.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareReportsChangedUid()
    {
        // Arrange
        var earlier = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 10, "mariadbd", 110, "mysql", false));
        var later = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 42, "evil", 1001, "alice", false));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.AllPorts, result.Outcome, "outcome");
        Assert.IsEmpty(result.NewListeners, "the endpoint is not new");
        Assert.HasCount(1, result.ChangedListeners, "changed");
        Assert.AreEqual(0L, result.ChangedOmitted, "omitted");

        var changed = result.ChangedListeners[0];

        Assert.AreEqual("tcp", changed.Proto, "proto");
        Assert.AreEqual(IPEndPoint.Parse("0.0.0.0:3306"), changed.Local, "local");
        Assert.AreEqual(42, changed.Pid, "the owner of the later snapshot");
        Assert.AreEqual("evil", changed.Command, "the command of the later snapshot");
        Assert.AreEqual((uint?)1001, changed.Uid, "uid now");
        Assert.AreEqual("alice", changed.User, "user now");
        Assert.IsFalse(changed.MixedUids, "mixed now");
        Assert.AreEqual((uint?)110, changed.PreviousUid, "uid before");
        Assert.IsFalse(changed.PreviousMixedUids, "mixed before");
    }

    /// <summary>
    /// A second UID on the endpoint with the lowest UID unchanged, a lower UID joining, and the lower socket leaving are reported with the flags of both sides.
    /// </summary>
    /// <param name="earlierUid">The lowest UID before</param>
    /// <param name="earlierMixed">Whether the endpoint was mixed before</param>
    /// <param name="laterUid">The lowest UID now</param>
    /// <param name="laterMixed">Whether the endpoint is mixed now</param>
    [TestMethod]
    [DataRow(107u, false, 107u, true)]
    [DataRow(1001u, false, 0u, true)]
    [DataRow(0u, true, 1001u, false)]
    [DataRow(0u, true, 1001u, true)]
    public void ListenerComparerCompareReportsTheMixedFlagsOfAChange(uint earlierUid, bool earlierMixed, uint laterUid, bool laterMixed)
    {
        // Arrange
        var earlier = Snapshot("all", Owned("udp", "0.0.0.0:5353", 0, string.Empty, earlierUid, "x", earlierMixed));
        var later = Snapshot("all", Owned("udp", "0.0.0.0:5353", 0, string.Empty, laterUid, "y", laterMixed));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.HasCount(1, result.ChangedListeners, "changed");
        Assert.AreEqual((uint?)laterUid, result.ChangedListeners[0].Uid, "uid now");
        Assert.AreEqual(laterMixed, result.ChangedListeners[0].MixedUids, "mixed now");
        Assert.AreEqual((uint?)earlierUid, result.ChangedListeners[0].PreviousUid, "uid before");
        Assert.AreEqual(earlierMixed, result.ChangedListeners[0].PreviousMixedUids, "mixed before");
    }

    /// <summary>
    /// The same UID and flag, a flag that only turns false, and a UID that is absent on either side report no change.
    /// </summary>
    /// <param name="earlierUid">The lowest UID before, or -1 when absent</param>
    /// <param name="earlierMixed">Whether the endpoint was mixed before</param>
    /// <param name="laterUid">The lowest UID now, or -1 when absent</param>
    /// <param name="laterMixed">Whether the endpoint is mixed now</param>
    [TestMethod]
    [DataRow(110L, false, 110L, false)]
    [DataRow(0L, true, 0L, true)]
    [DataRow(107L, true, 107L, false)]
    [DataRow(-1L, false, 1001L, false)]
    [DataRow(110L, false, -1L, false)]
    [DataRow(-1L, false, -1L, false)]
    public void ListenerComparerCompareUnchangedOrUnknownUidIsNotReported(long earlierUid, bool earlierMixed, long laterUid, bool laterMixed)
    {
        // Arrange
        var earlier = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 1, "a", earlierUid < 0 ? null : (uint)earlierUid, "x", earlierMixed));
        var later = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 1, "a", laterUid < 0 ? null : (uint)laterUid, "y", laterMixed));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.AllPorts, result.Outcome, "outcome");
        Assert.IsEmpty(result.ChangedListeners, "no change");
        Assert.AreEqual(0L, result.ChangedOmitted, "omitted");
    }

    /// <summary>
    /// An unconnected UDP socket in the kernel's local port range and a port above 1023 under a privileged comparison report no change, a port below 1024 does.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareChangeNotReportedOutsideComparedPorts()
    {
        // Arrange
        var earlier = Snapshot("privileged",
                               Owned("tcp", "0.0.0.0:22", 1, "sshd", 0, "root", false),
                               Owned("tcp", "0.0.0.0:8443", 2, "plesk", 1001, "u", false),
                               Owned("udp", "0.0.0.0:40000", 3, "x", 110, "u", false));
        var later = Snapshot("privileged",
                             Owned("tcp", "0.0.0.0:22", 1, "sshd", 33, "www-data", false),
                             Owned("tcp", "0.0.0.0:8443", 2, "plesk", 0, "root", true),
                             Owned("udp", "0.0.0.0:40000", 3, "x", 1001, "v", false));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreEqual(ListenerOutcome.PrivilegedOnly, result.Outcome, "outcome");
        Assert.AreSequenceEqual(["tcp 0.0.0.0:22"], Describe(result.ChangedListeners), "only the privileged port is reported");
    }

    /// <summary>
    /// On a comparison of all ports an unconnected UDP socket in the local port range is not reported and a TCP socket on the same port is.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareEphemeralUdpChangeIsNotReported()
    {
        // Arrange
        var earlier = Snapshot("all", Owned("tcp", "0.0.0.0:40000", 0, string.Empty, 110, "u", false), Owned("udp", "0.0.0.0:40000", 0, string.Empty, 110, "u", false), Owned("udp6", "[::]:50000", 0, string.Empty, 110, "u", false));
        var later = Snapshot("all", Owned("tcp", "0.0.0.0:40000", 0, string.Empty, 1001, "v", false), Owned("udp", "0.0.0.0:40000", 0, string.Empty, 1001, "v", false), Owned("udp6", "[::]:50000", 0, string.Empty, 1001, "v", false));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreSequenceEqual(["tcp 0.0.0.0:40000"], Describe(result.ChangedListeners), "changed");
    }

    /// <summary>
    /// An endpoint that is new is reported as new only, never as changed, and a change next to a new listener is reported in its own list.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareNewEndpointIsNotChanged()
    {
        // Arrange
        var earlier = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 1, "a", 110, "mysql", false));
        var later = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 2, "b", 1001, "alice", false), Owned("tcp", "0.0.0.0:4444", 3, "c", 1001, "alice", true));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.AreSequenceEqual(["tcp 0.0.0.0:4444"], Describe(result.NewListeners), "new");
        Assert.AreSequenceEqual(["tcp 0.0.0.0:3306"], Describe(result.ChangedListeners), "changed");
    }

    /// <summary>
    /// No change is reported when nothing is compared.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareNothingComparedReportsNoChange()
    {
        // Arrange
        var earlier = Snapshot("skipped", Owned("tcp", "0.0.0.0:22", 1, "a", 0, "root", false));
        var later = Snapshot("all", Owned("tcp", "0.0.0.0:22", 1, "a", 33, "www-data", false));

        // Act
        var skipped = ListenerComparer.Compare(earlier, later);
        var noBase = ListenerComparer.Compare(null, later);
        var cut = ListenerComparer.Compare(Snapshot("cut", Owned("tcp", "0.0.0.0:22", 1, "a", 0, "root", false)), later);

        // Assert
        Assert.IsEmpty(skipped.ChangedListeners, "not comparable");
        Assert.IsEmpty(noBase.ChangedListeners, "no earlier inventory");
        Assert.IsEmpty(cut.ChangedListeners, "privileged part cut");
    }

    /// <summary>
    /// A duplicate endpoint in a list does not throw: the first entry of the earlier list decides the previous UID.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareDuplicateEndpointDoesNotThrow()
    {
        // Arrange
        var earlier = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 1, "a", 110, "first", false), Owned("tcp", "0.0.0.0:3306", 1, "a", 5, "second", true));
        var later = Snapshot("all", Owned("tcp", "0.0.0.0:3306", 2, "b", 1001, "alice", false), Owned("tcp", "0.0.0.0:3306", 2, "b", 1001, "alice", false));

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.IsNotEmpty(result.ChangedListeners, "the change is reported");
        Assert.AreEqual((uint?)110, result.ChangedListeners[0].PreviousUid, "the first earlier entry wins");
        Assert.IsFalse(result.ChangedListeners[0].PreviousMixedUids, "and its flag");
    }

    /// <summary>
    /// At most 64 changes are listed in inventory order, the rest is counted, and the owner comes from the later snapshot.
    /// </summary>
    [TestMethod]
    public void ListenerComparerCompareListsAtMost64ChangesInInventoryOrder()
    {
        // Arrange
        var earlier = Snapshot("all", [.. Enumerable.Range(0, 70).Select(index => Owned("tcp", $"0.0.0.0:{1000 + index}", 1, "old", 110, "mysql", false))]);
        var later = Snapshot("all", [.. Enumerable.Range(0, 70).Select(index => Owned("tcp", $"0.0.0.0:{1000 + index}", 100 + index, $"cmd{index}", 1001, "alice", false))]);

        // Act
        var result = ListenerComparer.Compare(earlier, later);

        // Assert
        Assert.HasCount(ListenerComparer.MaxChangedListeners, result.ChangedListeners, "listed");
        Assert.AreEqual(6L, result.ChangedOmitted, "omitted");
        Assert.AreSequenceEqual(Describe(later.Listeners!.Take(64)), Describe(result.ChangedListeners), "the first 64 in inventory order");
        Assert.AreEqual(163, result.ChangedListeners[63].Pid, "PID of the later snapshot");
        Assert.AreEqual("cmd63", result.ChangedListeners[63].Command, "command of the later snapshot");
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
    /// Creates a listener with its owner and socket UID.
    /// </summary>
    /// <param name="proto">The protocol</param>
    /// <param name="local">The local endpoint</param>
    /// <param name="pid">The owner's PID</param>
    /// <param name="command">The owner's command</param>
    /// <param name="uid">The lowest socket UID, or <c>null</c> when unknown</param>
    /// <param name="user">The user name</param>
    /// <param name="mixed">Whether sockets of several UIDs share the endpoint</param>
    /// <returns>The listener</returns>
    private static Listener Owned(string proto, string local, int pid, string command, uint? uid, string user, bool mixed)
    {
        var listener = Listen(proto, local, pid, command);

        listener.Uid = uid;
        listener.User = user;
        listener.MixedUids = mixed;

        return listener;
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