namespace Vandox.Storage;

/// <summary>
/// The result of one fold step.
/// </summary>
public sealed class RollupStepResult
{
    #region Properties

    /// <summary>
    /// Gets the number of metric values folded.
    /// </summary>
    public int Values { get; init; }

    /// <summary>
    /// Gets the number of buckets written.
    /// </summary>
    public int Buckets { get; init; }

    /// <summary>
    /// Gets whether more values wait to be folded.
    /// </summary>
    public bool More { get; init; }

    #endregion // Properties
}