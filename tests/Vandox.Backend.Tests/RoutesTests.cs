using System.Reflection;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using Vandox.Backend.Components.Account;
using Vandox.Backend.Components.Pages;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for the authorization of the routable components
/// </summary>
[TestClass]
public class RoutesTests
{
    #region Methods

    /// <summary>
    /// Every routable component of the assembly except the login page requires authorization, and the login page is open and left out of the interactive routing.
    /// </summary>
    [TestMethod]
    public void RoutesEveryPageExceptLoginRequiresAuthorization()
    {
        // Arrange
        var pages = typeof(Login).Assembly.GetTypes().Where(type => type.GetCustomAttributes<RouteAttribute>(false).Any()).ToArray();
        var others = pages.Where(type => type != typeof(Login)).ToArray();

        // Act
        var unprotected = others.Except(others.Where(type => type.GetCustomAttributes<AuthorizeAttribute>(true).Any())).Select(type => type.FullName).ToArray();
        var anonymous = others.Where(type => type.GetCustomAttributes<AllowAnonymousAttribute>(true).Any()).Select(type => type.FullName).ToArray();

        // Assert
        Assert.Contains(typeof(Login), pages, "the login page is routable");
        Assert.Contains(typeof(OverviewPage), others, "the overview page is routable");
        Assert.IsEmpty(unprotected, "pages without [Authorize]: " + string.Join(", ", unprotected));
        Assert.IsEmpty(anonymous, "pages with [AllowAnonymous]: " + string.Join(", ", anonymous));
        Assert.IsNotEmpty(typeof(Login).GetCustomAttributes<AllowAnonymousAttribute>(false), "the login page allows anonymous access");
        Assert.IsNotEmpty(typeof(Login).GetCustomAttributes<ExcludeFromInteractiveRoutingAttribute>(false), "the login page is rendered statically");
    }

    /// <summary>
    /// The history page is routable at <c>/history</c> and requires authorization.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void RoutesHistoryPageRoutesToHistoryAndRequiresAuthorization()
    {
        // Arrange
        var type = typeof(HistoryPage);

        // Act
        var templates = type.GetCustomAttributes<RouteAttribute>(false).Select(route => route.Template).ToArray();

        // Assert
        Assert.Contains("/history", templates, "the history page is routed at /history");
        Assert.IsNotEmpty(type.GetCustomAttributes<AuthorizeAttribute>(true), "the history page requires authorization");
        Assert.IsEmpty(type.GetCustomAttributes<AllowAnonymousAttribute>(true), "the history page does not allow anonymous access");
    }

    #endregion // Methods
}