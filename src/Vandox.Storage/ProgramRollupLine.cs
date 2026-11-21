namespace Vandox.Storage;

/// <summary>
/// The line of one program in a per-program rollup read.
/// </summary>
public sealed class ProgramRollupLine
{
    #region Properties

    /// <summary>
    /// Gets the program name.
    /// </summary>
    public string Program { get; init; } = string.Empty;

    /// <summary>
    /// Gets the point of each step, <c>null</c> for a gap.
    /// </summary>
    public IReadOnlyList<ProgramRollupPoint?> Points { get; init; } = [];

    #endregion // Properties
}