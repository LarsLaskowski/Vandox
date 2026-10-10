using Microsoft.AspNetCore.Connections.Features;

namespace Vandox.Backend.Tests;

/// <summary>
/// A connection items feature backed by a dictionary.
/// </summary>
internal sealed class ConnectionItems : IConnectionItemsFeature
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionItems"/> class.
    /// </summary>
    /// <param name="items">The items</param>
    internal ConnectionItems(IDictionary<object, object?> items)
    {
        Items = items;
    }

    #endregion // Constructors

    #region IConnectionItemsFeature

    /// <inheritdoc />
    public IDictionary<object, object?> Items { get; set; }

    #endregion // IConnectionItemsFeature
}