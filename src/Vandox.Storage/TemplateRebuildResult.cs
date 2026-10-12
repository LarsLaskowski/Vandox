namespace Vandox.Storage;

/// <summary>
/// The result of a template rebuild.
/// </summary>
public sealed class TemplateRebuildResult
{
    #region Properties

    /// <summary>
    /// Gets the number of stored log lines counted (record IDs up to the high-water ID).
    /// </summary>
    public long Lines { get; init; }

    /// <summary>
    /// Gets the number of committed batch transactions.
    /// </summary>
    public int Batches { get; init; }

    /// <summary>
    /// Gets the number of rows of <c>log_templates</c> after the last batch.
    /// </summary>
    public long Templates { get; init; }

    #endregion // Properties
}