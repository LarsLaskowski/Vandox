using System.Text;
using System.Text.RegularExpressions;

namespace Vandox.Backend.Tests;

/// <summary>
/// Finds forms in the sources of the web UI that the content security policy must never have to catch: inline code, external URLs,
/// raw HTML sinks. A text scanner, not a parser; every method returns the distinct rule ids it found.
/// </summary>
internal static partial class WebSourceScanner
{
    #region Constants

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string NameBoundary = "(?<![\\w@:.\\-])";

    private const string UrlAttributes = "src|href|xlink:href|srcset|poster|action|formaction|data|ping";

    private const string ValuePattern = "(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)'|(?<v>[^\\s>]+))";

    #endregion // Constants

    #region Fields

    private static readonly char[] _controlsAndSpace = Enumerable.Range(0, 0x21).Select(code => (char)code).ToArray();

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Scans markup: <c>.razor</c> files and served HTML.
    /// </summary>
    /// <param name="markup">The markup</param>
    /// <returns>The distinct rule ids found</returns>
    internal static IReadOnlyList<string> FindInMarkup(string markup)
    {
        var rules = new List<string>();
        var text = RemoveComments(markup);

        Add(rules, StyleAttribute().IsMatch(text) || BareStyleAttribute().IsMatch(text), "style-attribute");
        Add(rules, StyleElement().IsMatch(text), "style-element");
        Add(rules, HasInlineScript(text), "inline-script");
        Add(rules, EventHandler().IsMatch(text), "event-handler");
        Add(rules, SrcDoc().IsMatch(text), "srcdoc");
        Add(rules, HttpEquiv().IsMatch(text), "http-equiv");

        foreach (Match match in UrlAttribute().Matches(text))
        {
            var value = match.Groups["v"].Value;
            var name = match.Groups["name"].Value;

            if (value.StartsWith('@'))
            {
                continue;
            }

            if (value.Contains('&'))
            {
                Add(rules, true, "character-reference");

                continue;
            }

            foreach (var token in IsListAttribute(name) ? ListTokens(value) : [value])
            {
                AddRule(rules, FindUrlRule(token));
            }
        }

        return rules;
    }

    /// <summary>
    /// Scans the code of a component: <c>.razor</c> and <c>.cs</c> files under <c>Components/</c>.
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>The distinct rule ids found</returns>
    internal static IReadOnlyList<string> FindInComponentCode(string code)
    {
        var rules = new List<string>();

        Add(rules, RawMarkup().IsMatch(code), "raw-markup");
        Add(rules, AttributeName().IsMatch(code), "attribute-name");

        return rules;
    }

    /// <summary>
    /// Scans an own script.
    /// </summary>
    /// <param name="script">The script</param>
    /// <returns>The distinct rule ids found</returns>
    internal static IReadOnlyList<string> FindInScript(string script)
    {
        var rules = new List<string>();

        Add(rules, script.Contains('\\'), "backslash");
        Add(rules, HasDoubleSlash(script), "double-slash");
        Add(rules, DynamicCode().IsMatch(script) || StringTimer().IsMatch(script), "dynamic-code");
        Add(rules, HtmlSink().IsMatch(script), "html-sink");
        Add(rules, SetAttribute().IsMatch(script), "set-attribute");
        Add(rules, GlobalNumberFunction().IsMatch(script), "global-number-function");

        return rules;
    }

    /// <summary>
    /// Scans a style sheet.
    /// </summary>
    /// <param name="styleSheet">The style sheet</param>
    /// <param name="ownStyleSheet">Whether the style sheet is an own one (every backslash is found) or the vendored one</param>
    /// <returns>The distinct rule ids found</returns>
    internal static IReadOnlyList<string> FindInStyleSheet(string styleSheet, bool ownStyleSheet)
    {
        var rules = new List<string>();

        Add(rules, styleSheet.Contains("@import", StringComparison.OrdinalIgnoreCase), "import");
        Add(rules, styleSheet.Contains("@font-face", StringComparison.OrdinalIgnoreCase), "font-face");
        Add(rules, ImageSet().IsMatch(styleSheet), "image-set");

        foreach (Match match in CssUrl().Matches(styleSheet))
        {
            var argument = new string(match.Groups["a"].Value.Where(character => character is not ('"' or '\'' or ' ' or '\t' or '\r' or '\n' or '\f')).ToArray());

            AddRule(rules, FindUrlRule(argument));
        }

        Add(rules, ownStyleSheet && styleSheet.Contains('\\'), "backslash");

        return rules;
    }

    /// <summary>
    /// Adds a rule id when it was found.
    /// </summary>
    /// <param name="rules">The rule ids found so far</param>
    /// <param name="found">Whether the rule was found</param>
    /// <param name="rule">The rule id</param>
    private static void Add(List<string> rules, bool found, string rule)
    {
        if (found)
        {
            AddRule(rules, rule);
        }
    }

    /// <summary>
    /// Adds a rule id once.
    /// </summary>
    /// <param name="rules">The rule ids found so far</param>
    /// <param name="rule">The rule id; <c>null</c> adds nothing</param>
    private static void AddRule(List<string> rules, string? rule)
    {
        if (rule is null || rules.Contains(rule))
        {
            return;
        }

        rules.Add(rule);
    }

    /// <summary>
    /// Tells whether an attribute holds a list of URLs.
    /// </summary>
    /// <param name="name">The name of the attribute</param>
    /// <returns><c>true</c> for <c>srcset</c> and <c>ping</c></returns>
    private static bool IsListAttribute(string name)
    {
        return name.Equals("srcset", StringComparison.OrdinalIgnoreCase) || name.Equals("ping", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits a list value into its tokens.
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The tokens</returns>
    private static string[] ListTokens(string value)
    {
        return value.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Reads a URL value as the URL parser of a browser does and returns the rule it breaks.
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The rule id, or <c>null</c> for a local URL</returns>
    private static string? FindUrlRule(string value)
    {
        var builder = new StringBuilder();

        foreach (var character in value)
        {
            if (character is '\t' or '\n' or '\r')
            {
                continue;
            }

            builder.Append(character == '\\' ? '/' : character);
        }

        var normalized = builder.ToString().Trim(_controlsAndSpace);

        if (UrlScheme().IsMatch(normalized))
        {
            return "scheme-url";
        }

        return normalized.StartsWith("//", StringComparison.Ordinal) ? "protocol-relative-url" : null;
    }

    /// <summary>
    /// Removes the HTML comments as the HTML parser ends them, but keeps a comment start inside a quoted attribute value.
    /// </summary>
    /// <param name="markup">The markup</param>
    /// <returns>The markup without comments</returns>
    private static string RemoveComments(string markup)
    {
        var builder = new StringBuilder();
        var inTag = false;
        var quote = '\0';
        var index = 0;

        while (index < markup.Length)
        {
            var character = markup[index];

            if (inTag)
            {
                (inTag, quote) = StepInTag(character, quote);
            }
            else if (string.CompareOrdinal(markup, index, "<!--", 0, 4) == 0)
            {
                index = EndOfComment(markup, index);
                builder.Append(' ');

                continue;
            }
            else
            {
                inTag = character == '<' && index + 1 < markup.Length && (char.IsAsciiLetter(markup[index + 1]) || markup[index + 1] == '/');
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Follows a tag character by character, respecting quoted attribute values.
    /// </summary>
    /// <param name="character">The next character</param>
    /// <param name="quote">The open quote, or <c>\0</c></param>
    /// <returns>Whether the tag goes on, and the open quote</returns>
    private static (bool InTag, char Quote) StepInTag(char character, char quote)
    {
        if (quote != '\0')
        {
            return (true, character == quote ? '\0' : quote);
        }

        if (character is '"' or '\'')
        {
            return (true, character);
        }

        return (character != '>', '\0');
    }

    /// <summary>
    /// Finds the end of a comment, including the abrupt forms.
    /// </summary>
    /// <param name="markup">The markup</param>
    /// <param name="start">The index of the comment start</param>
    /// <returns>The index after the comment</returns>
    private static int EndOfComment(string markup, int start)
    {
        if (string.CompareOrdinal(markup, start, "<!-->", 0, 5) == 0)
        {
            return start + 5;
        }

        if (string.CompareOrdinal(markup, start, "<!--->", 0, 6) == 0)
        {
            return start + 6;
        }

        var close = markup.IndexOf("-->", start + 4, StringComparison.Ordinal);
        var bang = markup.IndexOf("--!>", start + 4, StringComparison.Ordinal);

        if (close < 0 && bang < 0)
        {
            return markup.Length;
        }

        if (bang >= 0 && (close < 0 || bang < close))
        {
            return bang + 4;
        }

        return close + 3;
    }

    /// <summary>
    /// Tells whether a script start tag has no <c>src</c> attribute or an element body.
    /// </summary>
    /// <param name="text">The markup</param>
    /// <returns><c>true</c> when an inline script is found</returns>
    private static bool HasInlineScript(string text)
    {
        foreach (Match start in ScriptStart().Matches(text))
        {
            var attributesStart = start.Index + start.Length;
            var tagEnd = EndOfTag(text, attributesStart);
            var attributes = text[attributesStart..tagEnd];
            var sources = AttributeToken().Matches(attributes).Count(token => token.Groups["n"].Value.Equals("src", StringComparison.OrdinalIgnoreCase));
            var bodyStart = Math.Min(tagEnd + 1, text.Length);
            var end = text.IndexOf("</script", bodyStart, StringComparison.OrdinalIgnoreCase);
            var body = end < 0 ? text[bodyStart..] : text[bodyStart..end];

            if (sources == 0 || body.AsSpan().Trim().Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the end of a start tag, respecting quoted attribute values.
    /// </summary>
    /// <param name="text">The markup</param>
    /// <param name="start">The index inside the tag</param>
    /// <returns>The index of the closing bracket</returns>
    private static int EndOfTag(string text, int start)
    {
        var quote = '\0';
        var index = start;

        while (index < text.Length)
        {
            var character = text[index];

            if (quote != '\0')
            {
                quote = character == quote ? '\0' : quote;
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }

            index++;
        }

        return text.Length;
    }

    /// <summary>
    /// Tells whether a script has a double slash other than the start of a whole-line comment.
    /// </summary>
    /// <param name="script">The script</param>
    /// <returns><c>true</c> when one is found</returns>
    private static bool HasDoubleSlash(string script)
    {
        foreach (var line in script.Split('\n'))
        {
            var rest = line.TrimStart();

            rest = rest.StartsWith("//", StringComparison.Ordinal) ? rest[2..] : rest;

            if (rest.Contains("//", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex(NameBoundary + "style\\s*=", Options)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("<[a-z][^\\s/>]*(?:\\s+[^\\s\"'<>/=]+(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s\"'<>]+))?)*?\\s+style(?=[\\s/>=])", Options, 5000)]
    private static partial Regex BareStyleAttribute();

    [GeneratedRegex("<style(?=[\\s/>])", Options)]
    private static partial Regex StyleElement();

    [GeneratedRegex("<script(?=[\\s/>])", Options)]
    private static partial Regex ScriptStart();

    [GeneratedRegex("(?<n>[^\\s\"'<>/=]+)(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s\"'<>]+))?", Options)]
    private static partial Regex AttributeToken();

    [GeneratedRegex(NameBoundary + "on[a-z]+\\s*=", Options)]
    private static partial Regex EventHandler();

    [GeneratedRegex(NameBoundary + "srcdoc(?![\\w\\-])", Options)]
    private static partial Regex SrcDoc();

    [GeneratedRegex(NameBoundary + "http-equiv(?![\\w\\-])", Options)]
    private static partial Regex HttpEquiv();

    [GeneratedRegex(NameBoundary + "(?<name>" + UrlAttributes + ")\\s*=\\s*" + ValuePattern, Options)]
    private static partial Regex UrlAttribute();

    [GeneratedRegex("^[a-z][a-z0-9+.\\-]*:", Options)]
    private static partial Regex UrlScheme();

    [GeneratedRegex("\\b(?:MarkupString|AddMarkupContent)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex RawMarkup();

    [GeneratedRegex("AddAttribute\\(\\s*[^,]*,\\s*\"(?:style|srcdoc|http-equiv|on[a-z]+)\"\\s*[,)]", Options)]
    private static partial Regex AttributeName();

    [GeneratedRegex("\\b(?:eval|Function)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicCode();

    [GeneratedRegex("\\b(?:setTimeout|setInterval)\\s*\\(\\s*[\"'`]", RegexOptions.CultureInvariant)]
    private static partial Regex StringTimer();

    [GeneratedRegex("\\b(?:innerHTML|outerHTML|insertAdjacentHTML|write|writeln|setHTML|setHTMLUnsafe|parseHTMLUnsafe|createContextualFragment|DOMParser|parseFromString|srcdoc)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlSink();

    [GeneratedRegex("\\bsetAttribute", RegexOptions.CultureInvariant)]
    private static partial Regex SetAttribute();

    [GeneratedRegex("(?<![\\w$.])(?:(?:window|globalThis|self)\\s*\\.\\s*)?(?:parseInt|parseFloat|isNaN|isFinite)\\s*\\(", RegexOptions.CultureInvariant)]
    private static partial Regex GlobalNumberFunction();

    [GeneratedRegex("image-set\\s*\\(", Options)]
    private static partial Regex ImageSet();

    [GeneratedRegex("url\\s*\\((?<a>[^)]*)\\)", Options)]
    private static partial Regex CssUrl();

    #endregion // Patterns
}