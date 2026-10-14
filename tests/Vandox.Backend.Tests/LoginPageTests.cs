using System.Reflection;
using System.Security.Claims;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

using Vandox.Backend.Components.Account;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for the code of <see cref="Login"/> that the service tests reach only through the framework
/// </summary>
[TestClass]
public class LoginPageTests
{
    #region Constants

    private const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A post without a request context does nothing, the right password sends the browser to the home page, and a wrong one sets the status and shows no navigation.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginPageSubmitNavigatesOnlyAfterSignIn()
    {
        // Arrange
        using var fixture = new LoginServiceFixture();
        var navigation = new TestNavigationManager();
        var page = NewPage(navigation, fixture);
        var signedIn = LoginServiceFixture.NewContext("192.0.2.1");
        var wrong = LoginServiceFixture.NewContext("192.0.2.2");

        // Act
        await SubmitAsync(page);

        var navigationsWithoutContext = navigation.Navigations.Count;

        SetProperty(page, "HttpContext", signedIn.Context);
        SetProperty(page, "Password", LoginServiceFixture.Accepted);

        await SubmitAsync(page);

        var navigationsAfterSignIn = navigation.Navigations.ToArray();

        SetProperty(page, "HttpContext", wrong.Context);
        SetProperty(page, "Password", "a wrong password");

        await SubmitAsync(page);

        // Assert
        Assert.AreEqual(0, navigationsWithoutContext, "no navigation without a context");
        Assert.HasCount(1, navigationsAfterSignIn, "one navigation after the sign-in");
        Assert.AreEqual("/", navigationsAfterSignIn[0].Uri, "the home page");
        Assert.HasCount(1, signedIn.Authentication.SignIns, "signed in");
        Assert.HasCount(1, navigation.Navigations, "no navigation after a wrong password");
        Assert.AreEqual(StatusCodes.Status401Unauthorized, wrong.Context.Response.StatusCode, "status of a wrong password");
        Assert.IsEmpty(wrong.Authentication.SignIns, "not signed in");
    }

    /// <summary>
    /// A user who is already signed in is sent to the home page when the page is initialized, and an anonymous one is not.
    /// </summary>
    [TestMethod]
    public void LoginPageSendsSignedInUserToHomePage()
    {
        // Arrange
        using var fixture = new LoginServiceFixture();
        var navigation = new TestNavigationManager();
        var page = NewPage(navigation, fixture);
        var anonymous = new DefaultHttpContext();
        var authenticated = new DefaultHttpContext
                            {
                                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))
                            };

        // Act
        SetProperty(page, "HttpContext", anonymous);
        Initialize(page);

        var navigationsForAnonymous = navigation.Navigations.Count;

        SetProperty(page, "HttpContext", authenticated);
        Initialize(page);

        // Assert
        Assert.AreEqual(0, navigationsForAnonymous, "an anonymous user stays on the login page");
        Assert.HasCount(1, navigation.Navigations, "a signed-in user is sent away");
        Assert.AreEqual("/", navigation.Navigations[0].Uri, "the home page");
    }

    /// <summary>
    /// Creates the page with its services.
    /// </summary>
    /// <param name="navigation">The navigation</param>
    /// <param name="fixture">The login service</param>
    /// <returns>The page</returns>
    private static Login NewPage(NavigationManager navigation, LoginServiceFixture fixture)
    {
        var page = new Login();

        SetProperty(page, "Navigation", navigation);
        SetProperty(page, "LoginService", fixture.Service);

        return page;
    }

    /// <summary>
    /// Sets a private property of the page.
    /// </summary>
    /// <param name="page">The page</param>
    /// <param name="name">The name of the property</param>
    /// <param name="value">The value</param>
    private static void SetProperty(Login page, string name, object? value)
    {
        typeof(Login).GetProperty(name, NonPublic)!.SetValue(page, value);
    }

    /// <summary>
    /// Runs the handler of the form.
    /// </summary>
    /// <param name="page">The page</param>
    /// <returns>A task that completes when the handler is done</returns>
    private static Task SubmitAsync(Login page)
    {
        return (Task)typeof(Login).GetMethod("SubmitAsync", NonPublic)!.Invoke(page, null)!;
    }

    /// <summary>
    /// Runs the initialization of the page.
    /// </summary>
    /// <param name="page">The page</param>
    private static void Initialize(Login page)
    {
        typeof(Login).GetMethod("OnInitialized", NonPublic)!.Invoke(page, null);
    }

    #endregion // Methods
}