using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Tests;

/// <summary>
/// A <see cref="NavigationManager"/> whose base URI and current URI are <c>http://localhost/</c>, for components with a <see cref="Microsoft.AspNetCore.Components.Routing.NavLink"/>.
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

    #region NavigationManager

    /// <inheritdoc />
    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        throw new NotSupportedException("The test navigation manager does not navigate.");
    }

    #endregion // NavigationManager
}