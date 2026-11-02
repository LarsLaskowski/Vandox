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
    /// The largest number of changed listeners kept per comparison.
    /// </summary>
    public const int MaxChangedListeners = 64;

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
        var changed = FindChanged(earlier, later, privilegedOnly);

        return new ListenerComparison
               {
                   Outcome = privilegedOnly ? ListenerOutcome.PrivilegedOnly : ListenerOutcome.AllPorts,
                   NewListeners = [.. added.Take(MaxNewListeners)],
                   Omitted = Math.Max(0, added.Count - MaxNewListeners),
                   ChangedListeners = [.. changed.Take(MaxChangedListeners)],
                   ChangedOmitted = Math.Max(0, changed.Count - MaxChangedListeners)
               };
    }

    /// <summary>
    /// Finds the known endpoints of the later snapshot whose socket UID changed, in inventory order and each endpoint once.
    /// </summary>
    /// <param name="earlier">The earlier snapshot</param>
    /// <param name="later">The later snapshot</param>
    /// <param name="privilegedOnly">Whether only ports below 1024 are compared</param>
    /// <returns>The changed listeners</returns>
    private static List<ChangedListener> FindChanged(ConnectionSnapshot earlier, ConnectionSnapshot later, bool privilegedOnly)
    {
        var before = new Dictionary<(string Proto, IPEndPoint? Local), Listener>();

        foreach (var listener in earlier.Listeners ?? [])
        {
            before.TryAdd(Key(listener), listener);
        }

        var reported = new HashSet<(string Proto, IPEndPoint? Local)>();
        var changed = new List<ChangedListener>();

        foreach (var listener in later.Listeners ?? [])
        {
            var key = Key(listener);

            if (IsChangeCandidate(listener, privilegedOnly) && before.TryGetValue(key, out var earlierListener) && HasChangedUid(earlierListener, listener) && reported.Add(key))
            {
                changed.Add(ChangedFrom(listener, earlierListener));
            }
        }

        return changed;
    }

    /// <summary>
    /// Checks whether a listener of the later snapshot is compared for a changed socket UID: it has a local endpoint, is not an
    /// ephemeral UDP socket and lies in the compared ports.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <param name="privilegedOnly">Whether only ports below 1024 are compared</param>
    /// <returns><c>true</c> when the listener is compared</returns>
    private static bool IsChangeCandidate(Listener listener, bool privilegedOnly)
    {
        if (listener.Local is null || IsEphemeralUdp(listener))
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
    /// Checks whether the socket UID of an endpoint changed: both are known and the lowest UID differs or the endpoint turned mixed.
    /// </summary>
    /// <param name="earlier">The listener of the earlier snapshot</param>
    /// <param name="later">The listener of the later snapshot</param>
    /// <returns><c>true</c> when the socket UID changed</returns>
    private static bool HasChangedUid(Listener earlier, Listener later)
    {
        return earlier.Uid is not null && later.Uid is not null && (earlier.Uid != later.Uid || (later.MixedUids && earlier.MixedUids != later.MixedUids));
    }

    /// <summary>
    /// Builds a changed listener from the later entry and the earlier entry of the same endpoint.
    /// </summary>
    /// <param name="later">The listener of the later snapshot</param>
    /// <param name="earlier">The listener of the earlier snapshot</param>
    /// <returns>The changed listener</returns>
    private static ChangedListener ChangedFrom(Listener later, Listener earlier)
    {
        return new ChangedListener
               {
                   Proto = later.Proto,
                   Local = later.Local,
                   Pid = later.Pid,
                   Command = later.Command,
                   Uid = later.Uid,
                   User = later.User,
                   MixedUids = later.MixedUids,
                   PreviousUid = earlier.Uid,
                   PreviousMixedUids = earlier.MixedUids
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