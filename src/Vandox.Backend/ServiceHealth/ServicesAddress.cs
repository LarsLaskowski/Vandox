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
    internal AddressTimes Times { get; private init; } = new();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads an address from a query string.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <returns>The address; its error names the first invalid parameter</returns>
    public static ServicesAddress Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var address = new ServicesAddress();

        address.Error = address.Times.ReadParameters(QueryHelpers.ParseQuery(query));

        return address;
    }

    /// <summary>
    /// Copies the address.
    /// </summary>
    /// <returns>The copy</returns>
    public ServicesAddress Copy()
    {
        return new ServicesAddress
               {
                   Times = Times.Copy(),
                   Error = Error
               };
    }

    /// <summary>
    /// Gives the range: the fixed one, else the last 30 days up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <returns>The range</returns>
    public HistoryRange Range(DateTimeOffset now)
    {
        return Times.Range(now, HistoryPreset.ThirtyDays);
    }

    /// <summary>
    /// Writes the address as a query string after checking every value again.
    /// </summary>
    /// <returns>The query string, empty for the default address</returns>
    /// <exception cref="InvalidOperationException">The address has an error or a value the checks refuse</exception>
    public string ToQuery()
    {
        return Times.ToQuery(Error.Length > 0 ? Error : Times.Validate(), string.Empty, null, null);
    }

    /// <summary>
    /// Checks whether another address has the same preset, start and end.
    /// </summary>
    /// <param name="other">The other address</param>
    /// <returns><c>true</c> when the ranges are the same</returns>
    public bool SameRange(ServicesAddress other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Times.SameRange(other.Times);
    }

    #endregion // Methods
}