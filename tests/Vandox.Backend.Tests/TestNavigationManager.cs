using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Tests;

/// <summary>
/// A <see cref="NavigationManager"/> whose base URI and current URI are <c>http://localhost/</c>, for components with a <see cref="Microsoft.AspNetCore.Components.Routing.NavLink"/>; it records the navigations it is asked for and does not navigate.
/// </summary>
internal sealed class TestNavigationManager : NavigationManager
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TestNavigationManager"/> class.
    /// </summary>
    internal TestNavigationManager()
    {
        Initialize("http://localhost/", "http://localhost/");
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the navigations that were asked for: the address as given and the options.
    /// </summary>
    internal List<(string Uri, NavigationOptions Options)> Navigations { get; } = [];

    #endregion // Properties

    #region NavigationManager

    /// <inheritdoc />
    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        Navigations.Add((uri, options));
    }

    #endregion // NavigationManager
}