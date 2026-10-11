namespace Vandox.Core.LogParsing;

/// <summary>
/// The part of a snapshot that a <see cref="LegacyMonitorSnapshot"/> expects next.
/// </summary>
internal enum LegacySnapshotPhase
{
    /// <summary>
    /// The load average line.
    /// </summary>
    Load,

    /// <summary>
    /// The tasks line.
    /// </summary>
    Tasks,

    /// <summary>
    /// The CPU line.
    /// </summary>
    Cpu,

    /// <summary>
    /// The memory line.
    /// </summary>
    Memory,

    /// <summary>
    /// The swap line.
    /// </summary>
    Swap,

    /// <summary>
    /// The header of the process table.
    /// </summary>
    Table,

    /// <summary>
    /// The rows of the process table.
    /// </summary>
    Rows,

    /// <summary>
    /// The sections after the top part, which is accepted.
    /// </summary>
    Sections,

    /// <summary>
    /// The snapshot is skipped; its lines are not examined.
    /// </summary>
    Skipped
}