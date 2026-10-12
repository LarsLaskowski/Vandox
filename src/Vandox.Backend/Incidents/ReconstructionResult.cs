namespace Vandox.Backend.Incidents;

/// <summary>
/// The counts of an incident reconstruction run.
/// </summary>
public sealed class ReconstructionResult
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of log lines read.
    /// </summary>
    public long LinesRead { get; set; }

    /// <summary>
    /// Gets or sets the number of OOM kills.
    /// </summary>
    public long Kills { get; set; }

    /// <summary>
    /// Gets or sets the number of incidents built, the skipped ones included.
    /// </summary>
    public long Incidents { get; set; }

    /// <summary>
    /// Gets or sets the number of incidents created.
    /// </summary>
    public long Created { get; set; }

    /// <summary>
    /// Gets or sets the number of incidents updated.
    /// </summary>
    public long Updated { get; set; }

    /// <summary>
    /// Gets or sets the number of incidents left unchanged.
    /// </summary>
    public long Unchanged { get; set; }

    /// <summary>
    /// Gets or sets the number of stored incidents merged away.
    /// </summary>
    public long Merged { get; set; }

    /// <summary>
    /// Gets or sets the number of stored incidents no rebuilt one overlapped.
    /// </summary>
    public long Kept { get; set; }

    /// <summary>
    /// Gets or sets the number of incidents the store refused.
    /// </summary>
    public long Skipped { get; set; }

    #endregion // Properties
}