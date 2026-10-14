using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Logs;
using Vandox.Backend.Logs;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ExternalText"/>
/// </summary>
[TestClass]
public class ExternalTextTests
{
    #region Methods

    /// <summary>
    /// Markup in a plain run is shown as text, and an escaped run is marked.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ExternalTextRendersMarkupAsText()
    {
        // Arrange
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var runs = new[] { new TextRun("<b>x</b> ", false), new TextRun("\\u202e", true), new TextRun(" end", false) };
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                                                      {
                                                          [nameof(ExternalText.Runs)] = runs
                                                      });

        // Act
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ExternalText>(parameters)).ToHtmlString());

        // Assert
        Assert.Contains(HtmlEncoder.Default.Encode("<b>x</b> "), html, "the markup appears as text");
        Assert.DoesNotContain("<b>", html, "no markup from the text");
        Assert.Contains("<span class=\"escape\">\\u202e</span>", html, "the escaped run is inside a span of class escape");
        Assert.Contains("class=\"external\"", html, "the text is wrapped in an element of class external");
    }

    #endregion // Methods
}