using Vandox.Core.Model;

namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

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
        throw new NotImplementedException();
    }

    #endregion // Methods
}