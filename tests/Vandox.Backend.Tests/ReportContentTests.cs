using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Reports;
using Vandox.Backend.Reports;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportContent"/>
/// </summary>
[TestClass]
public class ReportContentTests
{
    #region Constants

    private const char Backslash = '\\';

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The pieces of the report element, in order, are those of the Markdown the command writes for the same data and language: every heading, paragraph, list item and table cell.
    /// </summary>
    /// <param name="language">The language</param>
    /// <param name="sample">The sample: the rich one has every part, the sparse one the variants the rich one cannot show</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en", "rich")]
    [DataRow("de", "rich")]
    [DataRow("en", "sparse")]
    [DataRow("de", "sparse")]
    public async Task ReportContentShowsTheSameContentAsTheMarkdown(string language, string sample)
    {
        // Arrange
        var text = ReportText.For(language);

        Assert.IsNotNull(text, "the language has a table");

        var summary = sample == "rich" ? RichSummary() : SparseSummary();
        var outages = sample == "rich" ? RichOutages() : [];
        var markdown = new StringWriter();
        var builder = new ReportViewBuilder(text, 0, ReportsView.PageOutages);

        await WriteAsync(new MarkdownReportWriter(markdown, text), summary, outages);
        await WriteAsync(builder, summary, outages);

        // Act
        var html = await RenderAsync(builder.Document);

        // Assert
        var expected = ReportPieces.FromMarkdown(markdown.ToString());
        var actual = ReportPieces.FromHtml(html);

        Assert.IsGreaterThan(20, expected.Count, "the Markdown has pieces");
        Assert.HasCount(expected.Count, actual, "the number of pieces");

        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"piece {index} of the report in {language}, {sample}");
        }
    }

    /// <summary>
    /// External text appears only as text inside a code element, with markup encoded, invisible characters written as visible escapes, a backtick as it is, a text over 2,048 characters cut with the marker and an empty text as a dash.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportContentShowsExternalTextOnlyAsEscapedText()
    {
        // Arrange
        var markup = "<script>alert(1)</script> & more";
        var invisible = string.Concat("a", (char)0x202E, "b", (char)0x200B, "c");

        var backtick = "a`b";
        var longText = new string('a', 3000);

        Assert.AreEqual((char)0x202E, invisible[1], "the input holds the right-to-left override");
        Assert.AreEqual((char)0x200B, invisible[3], "the input holds the zero-width space");

        var document = new ReportDocument
                       {
                           Language = "en",
                           Summary = [
                                         Heading("Head"),
                                         Item("Markup: ", ReportPiece.Code(markup)),
                                         Item("Invisible: ", ReportPiece.Code(invisible)),
                                         Item("Backtick: ", ReportPiece.Code(backtick)),
                                         Item("Long: ", ReportPiece.Code(longText)),
                                         Item("Empty: ", ReportPiece.Code(string.Empty))
                                     ]
                       };

        // Act
        var html = await RenderAsync(document);

        // Assert
        var pieces = ReportPieces.FromHtml(html);
        var code = "<code><span class=\"external\">";

        Assert.Contains(HtmlEncoder.Default.Encode(markup), html, "markup and the ampersand are encoded");
        Assert.DoesNotContain("<script", html, "no script element");
        Assert.DoesNotContain("</script", html, "no end tag of a script");
        Assert.Contains($"<span class=\"escape\">{Backslash}u202e</span>", html, "the right-to-left override is a visible escape");
        Assert.Contains($"<span class=\"escape\">{Backslash}u200b</span>", html, "the zero-width space is a visible escape");
        Assert.Contains(HtmlEncoder.Default.Encode(backtick), html, "the backtick stays a backtick");
        Assert.Contains("<span class=\"escape\">[cut: 952 more characters]</span>", html, "the marker of the cut");
        Assert.DoesNotContain(new string('a', 2049) + "[", html, "no more than 2,048 characters of the long text");
        Assert.DoesNotContain("<code></code>", html, "no empty code element");
        Assert.DoesNotContain("<code><span class=\"external\"></span></code>", html, "no empty external element");
        Assert.AreEqual(Count(html, code), Count(html, "class=\"external\""), "external text is only ever inside a code element");
        Assert.AreEqual(4, Count(html, code), "four texts are shown as code, the empty one is a dash");
        Assert.AreEqual($"Markup: {markup}", pieces[1], "the markup is text");
        Assert.AreEqual($"Invisible: a{Backslash}u202eb{Backslash}u200bc", pieces[2], "the invisible characters are written out");
        Assert.AreEqual($"Backtick: {backtick}", pieces[3], "the backtick");
        Assert.AreEqual($"Long: {new string('a', 2048)}[cut: 952 more characters]", pieces[4], "the long text ends with the marker");
        Assert.AreEqual("Empty: " + (char)0x2014, pieces[5], "the empty text is the dash of the Markdown");
    }

    /// <summary>
    /// Renders the content of a document the way the page does and returns the markup.
    /// </summary>
    /// <param name="document">The document</param>
    /// <returns>A task that returns the markup</returns>
    private static async Task<string> RenderAsync(ReportDocument document)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                                                      {
                                                          [nameof(ReportContent.Document)] = document
                                                      });

        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ReportContent>(parameters)).ToHtmlString());
    }

    /// <summary>
    /// Creates a heading of level 1.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The block</returns>
    private static ReportBlock Heading(string text)
    {
        return new ReportBlock
               {
                   Kind = ReportBlockKind.Heading,
                   Level = 1,
                   Pieces = [ReportPiece.Plain(text)]
               };
    }

    /// <summary>
    /// Creates a list item of a label and external text.
    /// </summary>
    /// <param name="label">The plain label</param>
    /// <param name="value">The external text</param>
    /// <returns>The block</returns>
    private static ReportBlock Item(string label, ReportPiece value)
    {
        return new ReportBlock
               {
                   Kind = ReportBlockKind.Item,
                   Pieces = [ReportPiece.Plain(label), value]
               };
    }

    /// <summary>
    /// Counts the occurrences of a text.
    /// </summary>
    /// <param name="text">The text to search in</param>
    /// <param name="value">The text to count</param>
    /// <returns>The number of occurrences</returns>
    private static int Count(string text, string value)
    {
        return text.Split(value).Length - 1;
    }

    #endregion // Methods
}