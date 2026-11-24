using Vandox.Backend.History;

namespace Vandox.Backend.Network;

/// <summary>
/// The address of the network view: the source, the range and the moment.
/// </summary>
public sealed class NetworkAddress
{
    #region Properties

    /// <summary>
    /// Gets or sets the source; <see langword="null"/> is the default source of the charts.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the preset range.
    /// </summary>
    public HistoryPreset? Preset { get; set; }

    /// <summary>
    /// Gets or sets the start of a fixed range.
    /// </summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>
    /// Gets or sets the end of a fixed range.
    /// </summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// Gets or sets the moment.
    /// </summary>
    public DateTimeOffset? At { get; set; }

    /// <summary>
    /// Gets or sets the first error of the address; empty when it is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads an address from a query string.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>The address; its error names the first invalid parameter</returns>
    public static NetworkAddress Parse(string query)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    // Skeleton: removed with the implementation
#pragma warning disable S2325
    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
    public NetworkAddress Copy()
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }
#pragma warning restore S2325

    // Skeleton: removed with the implementation
#pragma warning disable S2325
    /// <summary>
    /// Gives the range: the fixed one, else the preset up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <returns>The range</returns>
    public HistoryRange Range(DateTimeOffset now)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }
#pragma warning restore S2325

    // Skeleton: removed with the implementation
#pragma warning disable S2325
    /// <summary>
    /// Writes the address as a query string.
    /// </summary>
    /// <returns>The query string, empty for the default address</returns>
    public string ToQuery()
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }
#pragma warning restore S2325

    // Skeleton: removed with the implementation
#pragma warning disable S2325
    /// <summary>
    /// Checks whether another address shows the same charts: the same source, preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><c>true</c> when the charts are the same</returns>
    public bool SameChart(NetworkAddress other)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }
#pragma warning restore S2325

    #endregion // Methods
}