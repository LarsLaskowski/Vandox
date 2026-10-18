using System.Net;

using Vandox.Core.Model;

namespace Vandox.Core.Reports;

/// <summary>
/// Compares the listener inventories of two connection snapshots with the agent's rules.
/// </summary>
public static class ListenerComparer
{
    #region Constants

    /// <summary>
    /// The largest number of new listeners kept.
    /// </summary>
    public const int MaxNewListeners = 64;

    /// <summary>
    /// The first port that is not privileged.
    /// </summary>
    public const int PrivilegedPorts = 1024;

    /// <summary>
    /// The first port of the kernel's local port range.
    /// </summary>
    public const int EphemeralFirst = 32768;

    /// <summary>
    /// The last port of the kernel's local port range.
    /// </summary>
    public const int EphemeralLast = 60999;

    /// <summary>
    /// The inventory state of a whole inventory.
    /// </summary>
    private const string All = "all";

    /// <summary>
    /// The inventory state of an inventory cut inside its privileged part.
    /// </summary>
    private const string Cut = "cut";

    /// <summary>
    /// The inventory state of an inventory with a skipped table line.
    /// </summary>
    private const string Skipped = "skipped";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Compares two connection snapshots.
    /// </summary>
    /// <param name="earlier">The earlier snapshot, if any.</param>
    /// <param name="later">The later snapshot.</param>
    /// <returns>The comparison.</returns>
    public static ListenerComparison Compare(ConnectionSnapshot? earlier, ConnectionSnapshot later)
    {
        if (earlier is null)
        {
            return new ListenerComparison
                   {
                       Outcome = ListenerOutcome.NoEarlierInventory
                   };
        }

        if (IsNotComparable(earlier, later))
        {
            return new ListenerComparison
                   {
                       Outcome = ListenerOutcome.NotComparable
                   };
        }

        if (earlier.Inventory == Cut || later.Inventory == Cut)
        {
            return new ListenerComparison
                   {
                       Outcome = ListenerOutcome.PrivilegedIncomplete
                   };
        }

        var privilegedOnly = earlier.Inventory != All || later.Inventory != All;
        var known = (earlier.Listeners ?? []).Select(Key).ToHashSet();
        var added = (later.Listeners ?? []).Where(listener => IsNew(listener, known, privilegedOnly)).ToList();

        return new ListenerComparison
               {
                   Outcome = privilegedOnly ? ListenerOutcome.PrivilegedOnly : ListenerOutcome.AllPorts,
                   NewListeners = [.. added.Take(MaxNewListeners)],
                   Omitted = Math.Max(0, added.Count - MaxNewListeners)
               };
    }

    /// <summary>
    /// Checks whether two snapshots cannot be compared because of a skipped line, a missing inventory state or other tables.
    /// </summary>
    /// <param name="earlier">The earlier snapshot</param>
    /// <param name="later">The later snapshot</param>
    /// <returns><c>true</c> when the pair is not comparable</returns>
    private static bool IsNotComparable(ConnectionSnapshot earlier, ConnectionSnapshot later)
    {
        if (earlier.Inventory is null || later.Inventory is null || earlier.Inventory == Skipped || later.Inventory == Skipped)
        {
            return true;
        }

        var tables = new HashSet<string>(earlier.Tables ?? []);

        tables.SymmetricExceptWith(later.Tables ?? []);

        return tables.Count > 0;
    }

    /// <summary>
    /// Builds the key a listener is compared by: protocol and local endpoint.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <returns>The key</returns>
    private static (string Proto, IPEndPoint? Local) Key(Listener listener)
    {
        return (listener.Proto, listener.Local);
    }

    /// <summary>
    /// Checks whether a listener of the later snapshot is new and reported.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <param name="known">The keys of the earlier snapshot</param>
    /// <param name="privilegedOnly">Whether only ports below 1024 are compared</param>
    /// <returns><c>true</c> when the listener is new</returns>
    private static bool IsNew(Listener listener, HashSet<(string Proto, IPEndPoint? Local)> known, bool privilegedOnly)
    {
        if (listener.Local is null || IsEphemeralUdp(listener) || known.Contains(Key(listener)))
        {
            return false;
        }

        if (privilegedOnly)
        {
            return listener.Local.Port < PrivilegedPorts;
        }

        return true;
    }

    /// <summary>
    /// Checks whether a listener is an unconnected UDP socket in the kernel's local port range.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <returns><c>true</c> when the listener is not reported</returns>
    private static bool IsEphemeralUdp(Listener listener)
    {
        var port = listener.Local?.Port ?? 0;

        return (listener.Proto == Proto.Udp || listener.Proto == Proto.Udp6) && port >= EphemeralFirst && port <= EphemeralLast;
    }

    #endregion // Methods
}