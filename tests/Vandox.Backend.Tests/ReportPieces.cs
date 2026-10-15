using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Vandox.Backend.Tests;

/// <summary>
/// Reads the text pieces of a report: the headings, paragraphs, list items and table cells, in document order, from the Markdown of the command or from the
/// markup of the page, so that the two can be compared without the formatting of either.
/// </summary>
internal static partial class ReportPieces
{
    #region Constants

    private const string ReportClass = "report";

    #endregion // Constants

    #region Fields

    private static readonly string[] _collected = ["h1", "h2", "h3", "p", "th", "td", "li"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Reads the pieces of a Markdown report: the text of every heading, paragraph line, list item (without its marker, nested ones after their parent) and table
    /// cell. Separator rows of tables are skipped, a code span is replaced by its text and white space is collapsed.
    /// </summary>
    /// <param name="markdown">The Markdown</param>
    /// <returns>The pieces</returns>
    internal static IReadOnlyList<string> FromMarkdown(string markdown)
    {
        var pieces = new List<string>();

        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                AddCells(pieces, trimmed);
            }
            else
            {
                pieces.Add(Clean(StripMarker(trimmed)));
            }
        }

        return pieces;
    }

    /// <summary>
    /// Reads the pieces of the report element of a page: the text of every <c>h1</c> to <c>h3</c>, <c>p</c>, <c>th</c> and <c>td</c> and the own text of every
    /// <c>li</c> of the <c>article</c> with the class <c>report</c>, in document order, with tags removed, entities decoded and white space collapsed.
    /// </summary>
    /// <param name="html">The markup of the page</param>
    /// <returns>The pieces; empty when the page has no report element</returns>
    internal static IReadOnlyList<string> FromHtml(string html)
    {
        var start = ReportStart().Match(html);

        if (start.Success)
        {
            return ReadElements(html, start);
        }

        return [];
    }

    /// <summary>
    /// Collapses white space to single blanks and trims the text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The text</returns>
    internal static string Clean(string text)
    {
        return WhiteSpace().Replace(CodeSpan().Replace(text, "${text}"), " ").Trim();
    }

    /// <summary>
    /// Reads the pieces of the elements below the start of the report element.
    /// </summary>
    /// <param name="html">The markup of the page</param>
    /// <param name="start">The match of the start tag of the report element</param>
    /// <returns>The pieces</returns>
    private static string[] ReadElements(string html, Match start)
    {
        var slots = new List<string?>();
        var open = new Stack<(string Tag, int Slot, StringBuilder Text)>();
        var depth = 1;

        foreach (var token in Tokens().Matches(html[(start.Index + start.Length)..]).Cast<Match>())
        {
            if (token.Groups["text"].Success)
            {
                AppendText(open, token.Groups["text"].Value);

                continue;
            }

            if (token.Value.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            var tag = token.Groups["tag"].Value.ToLowerInvariant();
            var closing = token.Groups["close"].Length > 0;

            if (tag == "article")
            {
                depth += closing ? -1 : 1;
            }

            if (depth == 0)
            {
                break;
            }

            if (_collected.Contains(tag))
            {
                Visit(slots, open, tag, closing, token.Value.EndsWith("/>", StringComparison.Ordinal));
            }
        }

        return slots.Select(slot => slot ?? string.Empty).ToArray();
    }

    /// <summary>
    /// Adds text to the innermost open element, if there is one.
    /// </summary>
    /// <param name="open">The open elements</param>
    /// <param name="text">The text</param>
    private static void AppendText(Stack<(string Tag, int Slot, StringBuilder Text)> open, string text)
    {
        if (open.Count > 0)
        {
            open.Peek().Text.Append(text);
        }
    }

    /// <summary>
    /// Opens or closes an element that holds a piece.
    /// </summary>
    /// <param name="slots">The pieces in document order</param>
    /// <param name="open">The open elements</param>
    /// <param name="tag">The tag</param>
    /// <param name="closing">Whether it is an end tag</param>
    /// <param name="selfClosing">Whether it is an empty element</param>
    private static void Visit(List<string?> slots, Stack<(string Tag, int Slot, StringBuilder Text)> open, string tag, bool closing, bool selfClosing)
    {
        if (closing)
        {
            Close(slots, open, tag);
        }
        else if (selfClosing)
        {
            slots.Add(string.Empty);
        }
        else
        {
            slots.Add(null);
            open.Push((tag, slots.Count - 1, new StringBuilder()));
        }
    }

    /// <summary>
    /// Closes the innermost open element of a tag and stores its text in the slot it reserved when it opened.
    /// </summary>
    /// <param name="slots">The pieces in document order</param>
    /// <param name="open">The open elements</param>
    /// <param name="tag">The tag that closes</param>
    private static void Close(List<string?> slots, Stack<(string Tag, int Slot, StringBuilder Text)> open, string tag)
    {
        if (open.Count == 0 || open.Peek().Tag != tag)
        {
            return;
        }

        var (_, slot, text) = open.Pop();

        slots[slot] = WhiteSpace().Replace(WebUtility.HtmlDecode(text.ToString()), " ").Trim();
    }

    /// <summary>
    /// Adds the cells of a table row, unless it is the row below the header.
    /// </summary>
    /// <param name="pieces">The pieces</param>
    /// <param name="row">The row with its pipes</param>
    private static void AddCells(List<string> pieces, string row)
    {
        var cells = row.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

        if (cells.All(cell => Separator().IsMatch(cell)))
        {
            return;
        }

        pieces.AddRange(cells.Select(Clean));
    }

    /// <summary>
    /// Removes the heading or list marker at the start of a line.
    /// </summary>
    /// <param name="line">The trimmed line</param>
    /// <returns>The text</returns>
    private static string StripMarker(string line)
    {
        var heading = Heading().Match(line);

        if (heading.Success)
        {
            return line[heading.Length..];
        }

        return line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line;
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("<article\\b[^>]*\\bclass=\"[^\"]*\\b" + ReportClass + "\\b[^\"]*\"[^>]*>")]
    private static partial Regex ReportStart();

    [GeneratedRegex("<!--.*?-->|<(?<close>/?)(?<tag>[a-zA-Z][a-zA-Z0-9]*)(?:\\s[^>]*)?>|(?<text>[^<]+)", RegexOptions.Singleline)]
    private static partial Regex Tokens();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex("(`+) (?<text>.*?) \\1")]
    private static partial Regex CodeSpan();

    [GeneratedRegex("^:?-{3,}:?$")]
    private static partial Regex Separator();

    [GeneratedRegex("^#{1,3} ")]
    private static partial Regex Heading();

    #endregion // Patterns
}