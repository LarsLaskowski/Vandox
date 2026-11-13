namespace Vandox.Storage;

/// <summary>
/// A read of kernel event records above an ID for the kill scan.
/// </summary>
public sealed class KillScanQuery
{
    #region Properties

    /// <summary>
    /// Gets the record ID the read starts after.
    /// </summary>
    public long AboveId { get; init; }

    /// <summary>
    /// Gets the most records to return.
    /// </summary>
    public int Limit { get; init; }

    #endregion // Properties
}