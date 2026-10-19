namespace Vandox.Core.Reports;

/// <summary>
/// The result of comparing the listener inventories of two connection snapshots.
/// </summary>
public enum ListenerOutcome
{
    #region Values

    /// <summary>
    /// There is no earlier inventory to compare with.
    /// </summary>
    NoEarlierInventory,

    /// <summary>
    /// The two inventories cannot be compared.
    /// </summary>
    NotComparable,

    /// <summary>
    /// The privileged part of an inventory is incomplete.
    /// </summary>
    PrivilegedIncomplete,

    /// <summary>
    /// The inventories were compared on all ports.
    /// </summary>
    AllPorts,

    /// <summary>
    /// The inventories were compared on ports below 1024 only.
    /// </summary>
    PrivilegedOnly

    #endregion // Values
}