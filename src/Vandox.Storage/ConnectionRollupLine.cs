namespace Vandox.Storage;

/// <summary>
/// The line of one state or program in a connection rollup read.
/// </summary>
public sealed class ConnectionRollupLine
{
    #region Properties

    /// <summary>
    /// Gets the state or program name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the point of each step, <c>null</c> for a gap.
    /// </summary>
    public IReadOnlyList<ProgramRollupPoint?> Points { get; init; } = [];

    #endregion // Properties
}