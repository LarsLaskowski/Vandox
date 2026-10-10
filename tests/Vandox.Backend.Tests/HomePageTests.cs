using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for the components of the web UI shell
/// </summary>
[TestClass]
public class HomePageTests
{
    #region Methods

    /// <summary>
    /// The home page states that the backend runs.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task HomePageRendersBackendIsRunning()
    {
        // Arrange
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        // Act
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Home>()).ToHtmlString());

        // Assert
        Assert.Contains("<h1>Vandox backend</h1>", html, "heading");
        Assert.Contains("The backend is running.", html, "text");
    }

    #endregion // Methods
}