using Microsoft.AspNetCore.WebUtilities;

using Vandox.Backend.History;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// The address of the services view: the range and the moment.
/// </summary>
public sealed class ServicesAddress
{
    #region Properties

    /// <summary>
    /// Gets or sets the first error of the address; empty when it is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Gets the range and the moment.
    /// </summary>
    internal AddressTimes Times { get; } = new();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads an address from a query string.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>The address; its error names the first invalid parameter</returns>
#pragma warning disable RH2003 // Skeleton: removed with the implementation
    public static ServicesAddress Parse(string query)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003 // Skeleton: removed with the implementation

    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
#pragma warning disable S2325, RH2003 // Skeleton: removed with the implementation
    public ServicesAddress Copy()
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003 // Skeleton: removed with the implementation

    /// <summary>
    /// Gives the range: the fixed one, else the last 30 days up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <returns>The range</returns>
#pragma warning disable S2325, RH2003 // Skeleton: removed with the implementation
    public HistoryRange Range(DateTimeOffset now)
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003 // Skeleton: removed with the implementation

    /// <summary>
    /// Writes the address as a query string after checking every value again.
    /// </summary>
    /// <returns>The query string, empty for the default address</returns>
#pragma warning disable S2325, RH2003 // Skeleton: removed with the implementation
    public string ToQuery()
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003 // Skeleton: removed with the implementation

    /// <summary>
    /// Checks whether another address has the same preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><c>true</c> when the ranges are the same</returns>
#pragma warning disable S2325, RH2003 // Skeleton: removed with the implementation
    public bool SameRange(ServicesAddress other)
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003 // Skeleton: removed with the implementation

    #endregion // Methods
}