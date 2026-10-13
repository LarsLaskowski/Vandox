using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Components;
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
    /// The layout renders the logos, the main navigation, the theme switch and the body inside the main element.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MainLayoutRendersHeaderNavigationAndThemeSwitch()
    {
        // Arrange
        await using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new TestNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        RenderFragment body = builder => builder.AddMarkupContent(0, "<p>BODY-MARKER</p>");

        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                                                      {
                                                          [nameof(LayoutComponentBase.Body)] = body
                                                      });

        // Act
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MainLayout>(parameters)).ToHtmlString());

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
        // Arrange
        await using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new RecordingNavigationManager(address)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        RenderFragment body = builder => builder.AddMarkupContent(0, "<p>BODY-MARKER</p>");

        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                                                      {
                                                          [nameof(LayoutComponentBase.Body)] = body
                                                      });

        // Act
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MainLayout>(parameters)).ToHtmlString());

        // Assert
        var link = LogsLink().Match(html);

        Assert.IsTrue(link.Success, "the Logs link");
        Assert.Contains("href=\"logs\"", link.Groups[1].Value, "the Logs link points to logs");
        Assert.AreEqual(marked, ActiveClass().IsMatch(link.Groups[1].Value), $"the Logs link is marked: {marked}");
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("<img src=\"brand/vandox-logo-horizontal\\.svg\"[^>]*>")]
    private static partial Regex LightLogo();

    [GeneratedRegex("<img src=\"brand/vandox-logo-horizontal-dark\\.svg\"[^>]*>")]
    private static partial Regex DarkLogo();

    [GeneratedRegex("<a\\b([^>]*)>\\s*Overview\\s*</a>")]
    private static partial Regex OverviewLink();

    [GeneratedRegex("<a\\b([^>]*)>\\s*Logs\\s*</a>")]
    private static partial Regex LogsLink();

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