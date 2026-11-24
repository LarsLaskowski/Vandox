namespace Vandox.Storage;

/// <summary>
/// The figures of one remote address over the range of a connection rollup read.
/// </summary>
public sealed class ConnectionAddress
{
    #region Properties

    /// <summary>
    /// Gets the address.
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// Gets the average number of connections per snapshot of the range.
    /// </summary>
    public double Average { get; init; }

    /// <summary>
    /// Gets the largest number of connections in one snapshot.
    /// </summary>
    public double Maximum { get; init; }

    #endregion // Properties
}