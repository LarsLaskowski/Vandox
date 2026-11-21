namespace Vandox.Storage;

/// <summary>
/// The memory figure a per-program rollup read used.
/// </summary>
public enum ProgramMemoryKey
{
    /// <summary>
    /// Resident memory.
    /// </summary>
    Rss,

    /// <summary>
    /// Proportional memory.
    /// </summary>
    Pss
}