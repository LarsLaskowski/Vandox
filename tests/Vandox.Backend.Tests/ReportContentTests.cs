using System.Net;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Reports;
using Vandox.Backend.Logs;
using Vandox.Backend.Reports;
using Vandox.Core.Model;
using Vandox.Core.Reports;

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

    private const string EscapeStart = "<span class=\"escape\">";

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
    /// The pieces of the listener section on the page, in order, are those of the Markdown for the same data and language: the head, the findings with their listeners and the notes of the caps.
    /// </summary>
    /// <param name="language">The language</param>
    /// <param name="sample">The sample: the rich one, one in which every cap was hit, one with a start that was not compared, and one with hostile and long user names of sockets</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en", "rich")]
    [DataRow("de", "rich")]
    [DataRow("en", "capped")]
    [DataRow("de", "capped")]
    [DataRow("en", "uncompared")]
    [DataRow("de", "uncompared")]
    [DataRow("en", "hostile")]
    [DataRow("de", "hostile")]
    public async Task ReportContentShowsListenerSectionLikeTheMarkdown(string language, string sample)
    {
        // Arrange
        var text = ReportText.For(language);

        Assert.IsNotNull(text, "the language has a table");

        var at = new DateTimeOffset(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);
        var summary = sample switch
                      {
                          "rich" => RichSummary(),
                          "capped" => ListenerSummary(CappedListeners()),
                          "hostile" => ListenerSummary(Section(Tally("web-1", 50, at.AddDays(-4), at, 1, 1, 0, 0),
                                                               [
                                                                   StartFinding("web-1",
                                                                                9,
                                                                                at,
                                                                                ListenerOutcome.AllPorts,
                                                                                0,
                                                                                Entry(Proto.Tcp, "0.0.0.0", 23, 7, "telnetd", 0, "a`b <b>x</b> " + (char)0x202E, true),
                                                                                Entry(Proto.Tcp6, "::", 8443, uid: 33, user: new string('u', 40)),
                                                                                Entry(Proto.Tcp, "0.0.0.0", 24, uid: 7),
                                                                                Entry(Proto.Tcp, "0.0.0.0", 25))
                                                               ],
                                                               [])),
                          _ => ListenerSummary(Section(Tally("web-1", 50, at.AddDays(-4), at, 1, 0, 0, 1), [StartFinding("web-1", 9, at, ListenerOutcome.NotComparable, 0)], []))
                      };
        var markdown = new StringWriter();
        var builder = new ReportViewBuilder(text, 0, ReportsView.PageOutages);

        await WriteAsync(new MarkdownReportWriter(markdown, text), summary, []);
        await WriteAsync(builder, summary, []);

        // Act
        var html = await RenderAsync(builder.Document);

        // Assert
        var expected = ReportPieces.FromMarkdown(markdown.ToString());
        var actual = ReportPieces.FromHtml(html);
        var heading = language == "en" ? "New listeners" : "Neue Listener";

        Assert.Contains(heading, expected, "the Markdown has the section");
        Assert.Contains(heading, actual, "the page has the section");
        Assert.HasCount(expected.Count, actual, "the number of pieces");

        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"piece {index} of the report in {language}, {sample}");
        }
    }

    /// <summary>
    /// The command of an owner and the agent ID appear on the page only as text inside a code element: markup is encoded, the right-to-left override is a visible escape and no element comes from the command.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportContentShowsListenerCommandAsText()
    {
        // Arrange
        const string agent = "web`1";

        var command = "<b>x</b> ``` " + (char)0x202E + "\n";
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var summary = ListenerSummary(Section(Tally(agent, 3, at.AddDays(-1), at.AddDays(1), 1, 1, 0, 0),
                                              [StartFinding(agent, 2, at, ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 23, 7, command))],
                                              [AgentFinding(agent, 4, at.AddHours(2), ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 24, 8, command))]));
        var builder = new ReportViewBuilder(ReportText.English, 0, ReportsView.PageOutages);

        Assert.IsTrue(command.Contains((char)0x202E), "the command still holds the right-to-left override");

        await WriteAsync(builder, summary, []);

        // Act
        var html = await RenderAsync(builder.Document);

        // Assert
        var pieces = ReportPieces.FromHtml(html);
        var code = "<code><span class=\"external\">";

        Assert.DoesNotContain("<b>", html, "no element from the command");
        Assert.DoesNotContain("</b>", html, "no end tag from the command");
        Assert.Contains(HtmlEncoder.Default.Encode("<b>x</b>"), html, "the markup of the command is encoded");
        Assert.Contains($"<span class=\"escape\">{Backslash}u202e</span>", html, "the right-to-left override is a visible escape");

        var start = html.IndexOf(">New listeners</h2>", StringComparison.Ordinal);
        var end = start < 0 ? -1 : html.IndexOf(">Outages</h2>", start, StringComparison.Ordinal);

        Assert.IsGreaterThan(-1, start, "the rendered page has the heading of the listener section");
        Assert.IsGreaterThan(start, end, "the outages heading follows the listener section");
        Assert.AreEqual(5, Count(html[start..end], code), "the agent ID in the head and in both findings, and the two commands, are code in the listener section");
        Assert.AreEqual(Count(html, code), Count(html, "class=\"external\""), "external text is only ever inside a code element");
        Assert.Contains($"tcp 0.0.0.0:23, PID 7 <b>x</b> ``` {Backslash}u202e , socket UID unknown", pieces, "the command is text on the page (its line feed is white space there), followed by the unknown socket UID");
        Assert.Contains($"2026-10-01 14:00 UTC, agent {agent}, found by the agent, all ports: 1 new", pieces, "the agent ID is text on the page");
        Assert.AreEqual(2, Count(html, ", socket UID unknown"), "both listeners without a UID say so on the page");
    }

    /// <summary>
    /// The user of a socket appears on the page only as text inside a code element: markup is encoded, a backtick run and the right-to-left override stay text or a visible escape, and no element comes from the name.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportContentShowsSocketUserAsText()
    {
        // Arrange
        const string agent = "web1";

        var user = "<b>x</b> ``` " + (char)0x202E + "\n";
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var summary = ListenerSummary(Section(Tally(agent, 3, at.AddDays(-1), at.AddDays(1), 1, 1, 0, 0),
                                              [StartFinding(agent, 2, at, ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 23, 7, "telnetd", 5, user))],
                                              []));
        var builder = new ReportViewBuilder(ReportText.English, 0, ReportsView.PageOutages);

        Assert.IsTrue(user.Contains((char)0x202E), "the user still holds the right-to-left override");

        await WriteAsync(builder, summary, []);

        // Act
        var html = await RenderAsync(builder.Document);

        // Assert
        var pieces = ReportPieces.FromHtml(html);
        var start = html.IndexOf(">New listeners</h2>", StringComparison.Ordinal);
        var end = start < 0 ? -1 : html.IndexOf(">Outages</h2>", start, StringComparison.Ordinal);

        Assert.IsGreaterThan(-1, start, "the rendered page has the heading of the listener section");
        Assert.IsGreaterThan(start, end, "the outages heading follows the listener section");
        Assert.DoesNotContain("<b>", html, "no element from the user");
        Assert.DoesNotContain("</b>", html, "no end tag from the user");
        Assert.Contains(HtmlEncoder.Default.Encode("<b>x</b>"), html, "the markup of the user is encoded");
        Assert.Contains($"<span class=\"escape\">{Backslash}u202e</span>", html, "the right-to-left override is a visible escape");
        Assert.AreEqual(4, Count(html[start..end], "<code><span class=\"external\">"), "the agent ID in the head and in the finding, the command and the user are code in the listener section");
        Assert.Contains(piece => piece.StartsWith($"tcp 0.0.0.0:23, PID 7 telnetd, socket UID 5 <b>x</b> ``` {Backslash}u202e", StringComparison.Ordinal), pieces, "the user is text on the page, after the socket UID");
    }

    /// <summary>
    /// A command whose plain and escaped characters alternate is shown in at most 16 runs: 15 runs and its rest in one escaped run, with the same characters as its escaped text and
    /// the whole marker of the cut; a short command with one escape is shown as before.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ReportContentCapsTheRunsOfExternalText()
    {
        // Arrange
        const string agent = "web1";

        var command = Alternating(1024);
        var shortCommand = string.Concat("a", (char)1, "a");
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var summary = ListenerSummary(Section(Tally(agent, 3, at.AddDays(-1), at.AddDays(1), 1, 1, 0, 0),
                                              [StartFinding(agent, 2, at, ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 23, 7, command), Entry(Proto.Tcp, "0.0.0.0", 24, 8, shortCommand))],
                                              []));
        var builder = new ReportViewBuilder(ReportText.English, 0, ReportsView.PageOutages);

        Assert.AreEqual((char)1, command[1], "the command holds the control character, so the escape survived");
        Assert.AreEqual((char)1, shortCommand[1], "the short command holds the control character, so the escape survived");

        await WriteAsync(builder, summary, []);

        // Act
        var html = await RenderAsync(builder.Document);

        // Assert
        var spans = ExternalSpans(html).Where(span => span.Contains("[cut: ", StringComparison.Ordinal)).ToList();

        Assert.HasCount(1, spans, "one external text is cut: the long command");
        Assert.AreEqual(8, Count(spans[0], "class=\"escape\""), "16 runs: 8 plain, 7 escaped and one escaped rest");
        Assert.AreEqual(LogText.Escape(command, LogText.MaxShortChars), Decode(spans[0]), "the shown characters are those of the escaped text");
        Assert.EndsWith("[cut: 439 more characters]", Decode(spans[0]), "the shown text ends with the whole marker of the cut");
        Assert.Contains($"<code><span class=\"external\">a<span class=\"escape\">{Backslash}u0001</span>a</span></code>", html, "the short command with one escape is shown as before");
    }

    /// <summary>
    /// The rest of an external text whose escapes and cut leave it as long as its source text is shown in the escape style on the page.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [TestCategory("AC5")]
    public async Task ReportContentShowsARestOfEqualLengthAsEscape()
    {
        // Arrange
        var text = Alternating(16) + new string('a', 2022);
        var document = new ReportDocument
                       {
                           Language = "en",
                           Summary = [
                                         Heading("Head"),
                                         Item("Rest: ", ReportPiece.Code(text))
                                     ]
                       };

        Assert.AreEqual((char)1, text[15], "the text holds the control character, so the escape survived");

        // Act
        var html = await RenderAsync(document);

        // Assert
        var spans = ExternalSpans(html);

        Assert.HasCount(1, spans, "one external text");

        var lastEscape = spans[0][(spans[0].LastIndexOf(EscapeStart, StringComparison.Ordinal) + EscapeStart.Length)..];

        lastEscape = lastEscape[..lastEscape.IndexOf("</span>", StringComparison.Ordinal)];

        Assert.AreEqual(LogText.Escape(text[15..], 1998), WebUtility.HtmlDecode(lastEscape), "the last escape span holds the whole rest");
        Assert.EndsWith("[cut: 30 more characters]", WebUtility.HtmlDecode(lastEscape), "the rest ends with the whole marker of the cut");
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
    /// Builds a text whose plain character <c>a</c> and control character U+0001 alternate, starting with the plain one.
    /// </summary>
    /// <param name="length">The length in characters</param>
    /// <returns>The text</returns>
    private static string Alternating(int length)
    {
        var characters = new char[length];

        for (var position = 0; position < length; position++)
        {
            characters[position] = position % 2 == 0 ? 'a' : (char)1;
        }

        return new string(characters);
    }

    /// <summary>
    /// Finds the markup inside every external element, the escape elements included.
    /// </summary>
    /// <param name="html">The markup of the page</param>
    /// <returns>The inner markup of each external element, in order</returns>
    private static List<string> ExternalSpans(string html)
    {
        const string start = "<code><span class=\"external\">";
        const string end = "</span></code>";

        var spans = new List<string>();
        var index = html.IndexOf(start, StringComparison.Ordinal);

        while (index >= 0)
        {
            var from = index + start.Length;
            var to = html.IndexOf(end, from, StringComparison.Ordinal);

            spans.Add(html[from..to]);

            index = html.IndexOf(start, to, StringComparison.Ordinal);
        }

        return spans;
    }

    /// <summary>
    /// Turns the inner markup of an external element into the text it shows.
    /// </summary>
    /// <param name="markup">The inner markup</param>
    /// <returns>The shown text</returns>
    private static string Decode(string markup)
    {
        return WebUtility.HtmlDecode(markup.Replace(EscapeStart, string.Empty, StringComparison.Ordinal).Replace("</span>", string.Empty, StringComparison.Ordinal));
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