using System.Security.Claims;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Layout;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="MainLayout"/>
/// </summary>
[TestClass]
public partial class MainLayoutTests
{
    #region Methods

    /// <summary>
    /// The layout of a signed-in user renders the logos, the main navigation, the theme switch and the body inside the main element.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MainLayoutRendersHeaderNavigationAndThemeSwitch()
    {
        // Arrange / Act
        var html = await RenderAsync(SignedIn());

        // Assert
        var light = LightLogo().Match(html);
        var dark = DarkLogo().Match(html);
        var link = OverviewLink().Match(html);
        var toggle = ThemeButton().Match(html);

        Assert.Contains("<header", html, "header");
        Assert.IsTrue(light.Success, "light logo");
        Assert.Contains("class=\"logo-light\"", light.Value, "light logo class");
        Assert.Contains("alt=\"Vandox\"", light.Value, "light logo alternative text");
        Assert.IsTrue(dark.Success, "dark logo");
        Assert.Contains("class=\"logo-dark\"", dark.Value, "dark logo class");
        Assert.Contains("alt=\"Vandox\"", dark.Value, "dark logo alternative text");
        Assert.Contains("<nav aria-label=\"Main\">", html, "main navigation");
        Assert.IsTrue(link.Success, "the Overview link");
        Assert.Contains("href=\"\"", link.Groups[1].Value, "the Overview link points to the base URI");
        Assert.IsTrue(ActiveClass().IsMatch(link.Groups[1].Value), "the Overview link is active on the base URI");
        Assert.IsTrue(toggle.Success, "theme button");
        Assert.IsTrue(ToggleAttribute().IsMatch(toggle.Groups[1].Value), "theme button attribute");
        Assert.Contains("System", toggle.Groups[2].Value, "theme text System");
        Assert.Contains("Light", toggle.Groups[2].Value, "theme text Light");
        Assert.Contains("Dark", toggle.Groups[2].Value, "theme text Dark");
        Assert.IsTrue(BodyInMain().IsMatch(html), "the body is inside main");
    }

    /// <summary>
    /// The Logs entry points to the logs and is marked on both log pages, but not on the overview.
    /// </summary>
    /// <param name="address">The current address</param>
    /// <param name="marked">Whether the Logs entry is marked</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("http://localhost/logs", true)]
    [DataRow("http://localhost/logs?q=disk", true)]
    [DataRow("http://localhost/logs/templates", true)]
    [DataRow("http://localhost/", false)]
    public async Task MainLayoutMarksLogsOnTheLogPages(string address, bool marked)
    {
        // Act
        var html = await RenderAsync(SignedIn(), address);

        // Assert
        var link = LogsLink().Match(html);

        Assert.IsTrue(link.Success, "the Logs link");
        Assert.Contains("href=\"logs\"", link.Groups[1].Value, "the Logs link points to logs");
        Assert.AreEqual(marked, ActiveClass().IsMatch(link.Groups[1].Value), $"the Logs link is marked: {marked}");
    }

    /// <summary>
    /// The Incidents entry points to the incidents and is marked on the list and on an incident page, but not on the logs or the overview.
    /// </summary>
    /// <param name="address">The current address</param>
    /// <param name="marked">Whether the Incidents entry is marked</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("http://localhost/incidents", true)]
    [DataRow("http://localhost/incidents/7", true)]
    [DataRow("http://localhost/logs", false)]
    [DataRow("http://localhost/", false)]
    public async Task MainLayoutMarksIncidentsOnTheIncidentPages(string address, bool marked)
    {
        // Act
        var html = await RenderAsync(SignedIn(), address);

        // Assert
        var link = IncidentsLink().Match(html);

        Assert.IsTrue(link.Success, "the Incidents link");
        Assert.Contains("href=\"incidents\"", link.Groups[1].Value, "the Incidents link points to incidents");
        Assert.AreEqual(marked, ActiveClass().IsMatch(link.Groups[1].Value), $"the Incidents link is marked: {marked}");
    }

    /// <summary>
    /// The Report entry points to the report and is marked on the report page, with or without a language, but not on the incidents or the overview.
    /// </summary>
    /// <param name="address">The current address</param>
    /// <param name="marked">Whether the Report entry is marked</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("http://localhost/report", true)]
    [DataRow("http://localhost/report?lang=de", true)]
    [DataRow("http://localhost/incidents", false)]
    [DataRow("http://localhost/", false)]
    public async Task MainLayoutMarksReportOnTheReportPage(string address, bool marked)
    {
        // Act
        var html = await RenderAsync(SignedIn(), address);

        // Assert
        var link = ReportLink().Match(html);

        Assert.IsTrue(link.Success, "the Report link");
        Assert.Contains("href=\"report\"", link.Groups[1].Value, "the Report link points to report");
        Assert.AreEqual(marked, ActiveClass().IsMatch(link.Groups[1].Value), $"the Report link is marked: {marked}");
    }

    /// <summary>
    /// The sign-out form and the main navigation are shown to a signed-in user only: the form posts to <c>logout</c> with an antiforgery token.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MainLayoutShowsSignOutAndNavigationOnlyWhenSignedIn()
    {
        // Arrange
        var signedIn = SignedIn();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        // Act
        var signedInHtml = await RenderAsync(signedIn);
        var anonymousHtml = await RenderAsync(anonymous);

        // Assert
        var form = SignOutForm().Match(signedInHtml);

        Assert.IsTrue(form.Success, "the signed-in layout has a form");
        Assert.IsTrue(MethodPost().IsMatch(form.Value), "the form posts");
        Assert.IsTrue(ActionLogout().IsMatch(form.Value), "the form posts to logout");
        Assert.IsTrue(AntiforgeryInput().IsMatch(form.Value), "the form carries the antiforgery token");
        Assert.Contains("Sign out", form.Value, "the button says Sign out");
        Assert.DoesNotContain("Sign out", anonymousHtml, "the anonymous layout has no sign-out button");
        Assert.DoesNotContain("action=\"logout\"", anonymousHtml, "the anonymous layout has no sign-out form");
        Assert.Contains("<nav aria-label=\"Main\">", signedInHtml, "the signed-in layout has the main navigation");
        Assert.DoesNotContain("<nav", anonymousHtml, "the anonymous layout has no navigation");
        Assert.IsTrue(LightLogo().IsMatch(anonymousHtml), "the anonymous layout keeps the logo");
        Assert.IsTrue(ThemeButton().IsMatch(anonymousHtml), "the anonymous layout keeps the theme switch");
    }

    /// <summary>
    /// Creates a signed-in user.
    /// </summary>
    /// <returns>The user</returns>
    private static ClaimsPrincipal SignedIn()
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"));
    }

    /// <summary>
    /// Renders the layout for a user.
    /// </summary>
    /// <param name="user">The user</param>
    /// <param name="address">The current address</param>
    /// <returns>A task that returns the markup</returns>
    private static async Task<string> RenderAsync(ClaimsPrincipal user, string address = "http://localhost/")
    {
        await using var services = new ServiceCollection().AddLogging()
                                                          .AddSingleton<NavigationManager>(new RecordingNavigationManager(address))
                                                          .AddAuthorizationCore()
                                                          .AddCascadingAuthenticationState()
                                                          .AddSingleton<AuthenticationStateProvider>(new FixedAuthenticationStateProvider(user))
                                                          .AddSingleton<AntiforgeryStateProvider, FixedAntiforgeryStateProvider>()
                                                          .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        RenderFragment body = builder => builder.AddMarkupContent(0, "<p>BODY-MARKER</p>");

        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                                                      {
                                                          [nameof(LayoutComponentBase.Body)] = body
                                                      });

        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MainLayout>(parameters)).ToHtmlString());
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("<form\\b[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex SignOutForm();

    [GeneratedRegex("\\bmethod=\"post\"")]
    private static partial Regex MethodPost();

    [GeneratedRegex("\\baction=\"logout\"")]
    private static partial Regex ActionLogout();

    [GeneratedRegex("<input\\b[^>]*\\bname=\"__RequestVerificationToken\"[^>]*\\bvalue=\"fixed-antiforgery-token\"|<input\\b[^>]*\\bvalue=\"fixed-antiforgery-token\"[^>]*\\bname=\"__RequestVerificationToken\"")]
    private static partial Regex AntiforgeryInput();

    [GeneratedRegex("<img src=\"brand/vandox-logo-horizontal\\.svg\"[^>]*>")]
    private static partial Regex LightLogo();

    [GeneratedRegex("<img src=\"brand/vandox-logo-horizontal-dark\\.svg\"[^>]*>")]
    private static partial Regex DarkLogo();

    [GeneratedRegex("<a\\b([^>]*)>\\s*Overview\\s*</a>")]
    private static partial Regex OverviewLink();

    [GeneratedRegex("<a\\b([^>]*)>\\s*Logs\\s*</a>")]
    private static partial Regex LogsLink();

    [GeneratedRegex("<a\\b([^>]*)>\\s*Incidents\\s*</a>")]
    private static partial Regex IncidentsLink();

    [GeneratedRegex("<a\\b([^>]*)>\\s*Report\\s*</a>")]
    private static partial Regex ReportLink();

    [GeneratedRegex("<button type=\"button\"([^>]*)>(.*?)</button>", RegexOptions.Singleline)]
    private static partial Regex ThemeButton();

    [GeneratedRegex("class=\"[^\"]*\\bactive\\b[^\"]*\"")]
    private static partial Regex ActiveClass();

    [GeneratedRegex("(^|\\s)data-theme-toggle(\\s|=|$)")]
    private static partial Regex ToggleAttribute();

    [GeneratedRegex("<main>.*<p>BODY-MARKER</p>.*</main>", RegexOptions.Singleline)]
    private static partial Regex BodyInMain();

    #endregion // Patterns
}