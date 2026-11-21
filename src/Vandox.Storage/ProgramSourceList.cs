namespace Vandox.Storage;

/// <summary>
/// The sources that have program rollups in a range.
/// </summary>
public sealed class ProgramSourceList
{
    #region Properties

    /// <summary>
    /// Gets the sources, by name.
    /// </summary>
    public IReadOnlyList<ProgramSource> Sources { get; init; } = [];

    /// <summary>
    /// Gets whether more sources exist than the list holds.
    /// </summary>
    public bool More { get; init; }

    #endregion // Properties
}