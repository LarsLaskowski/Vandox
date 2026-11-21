namespace Vandox.Storage;

/// <summary>
/// The measure a per-program rollup read ranks and reports.
/// </summary>
public enum ProgramMeasure
{
    /// <summary>
    /// Memory: proportional memory when every snapshot of the range has it, else resident memory.
    /// </summary>
    Memory,

    /// <summary>
    /// CPU percentage.
    /// </summary>
    Cpu
}