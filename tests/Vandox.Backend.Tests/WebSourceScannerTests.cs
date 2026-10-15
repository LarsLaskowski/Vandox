namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="WebSourceScanner"/>: every form it must find has a row that names the one rule it breaks, so an input that lost a
/// backslash finds another rule or none and fails.
/// </summary>
[TestClass]
public class WebSourceScannerTests
{
    #region Methods

    /// <summary>
    /// Markup that breaks a rule is found, and only that rule.
    /// </summary>
    /// <param name="markup">The markup</param>
    /// <param name="rule">The only rule id expected</param>
    [DataRow("<p style=\"color:red\">", "style-attribute")]
    [DataRow("<p STYLE = \"color:red\">", "style-attribute")]
    [DataRow("<style>p{}</style>", "style-element")]
    [DataRow("<Style>p{}</Style>", "style-element")]
    [DataRow("<script>alert(1)</script>", "inline-script")]
    [DataRow("<SCRIPT src=\"js/app.js\">alert(1)</SCRIPT>", "inline-script")]
    [DataRow("<button onclick=\"x()\">", "event-handler")]
    [DataRow("<button onClick = \"x()\">", "event-handler")]
    [DataRow("<body ONLOAD=x()>", "event-handler")]
    [DataRow("<a href=\"javascript:x()\">", "scheme-url")]
    [DataRow("<a href=\" JavaScript:x()\">", "scheme-url")]
    [DataRow("<a href=\"java\tscript:x()\">", "scheme-url")]
    [DataRow("<a href=javascript:x()>", "scheme-url")]
    [DataRow("<a href=\"jav&#x61;script:x()\">", "character-reference")]
    [DataRow("<a href=\"javascript&colon;x()\">", "character-reference")]
    [DataRow("<script src=\"HTTPS://cdn.example/x.js\"></script>", "scheme-url")]
    [DataRow("<img src=\"Data:image/png;base64,AA\">", "scheme-url")]
    [DataRow("<img src=\"//cdn.example/x.png\">", "protocol-relative-url")]
    [DataRow("<img src=\"\\\\cdn.example/x.png\">", "protocol-relative-url")]
    [DataRow("<img src=\"/\\cdn.example/x.png\">", "protocol-relative-url")]
    [DataRow("<img src=\"\\/cdn.example/x.png\">", "protocol-relative-url")]
    [DataRow("<img src=\"/\t/cdn.example/x.png\">", "protocol-relative-url")]
    [DataRow("<img srcset=\"brand/a.svg 1x, //cdn.example/x.png 2x\">", "protocol-relative-url")]
    [DataRow("<video poster=\"https://cdn.example/x.png\">", "scheme-url")]
    [DataRow("<form action=\"https://x.example/\">", "scheme-url")]
    [DataRow("<button formaction=\"//x.example/\">", "protocol-relative-url")]
    [DataRow("<object data=\"https://x.example/x.svg\">", "scheme-url")]
    [DataRow("<use xlink:href=\"https://x.example/x.svg#a\">", "scheme-url")]
    [DataRow("<a href=\"a\" ping=\"https://x.example/p\">", "scheme-url")]
    [DataRow("<iframe srcdoc=\"x\">", "srcdoc")]
    [DataRow("<meta http-equiv=\"refresh\" content=\"0;url=https://x.example/\">", "http-equiv")]
    [DataRow("<!-- x --!><p style=\"x\">", "style-attribute")]
    [DataRow("<p title=\"<!--\" style=\"x\" class=\"-->\">", "style-attribute")]
    [DataRow("<meta title=\"<!--\" http-equiv=\"refresh\" content=\"0;url=https://x.example/\" class=\"-->\">", "http-equiv")]
    [DataRow("<script data-src=\"x\">alert(1)</script>", "inline-script")]
    [TestMethod]
    public void WebSourceScannerMarkupFindsForbiddenForms(string markup, string rule)
    {
        // Act
        var found = WebSourceScanner.FindInMarkup(markup);

        // Assert
        Assert.AreSequenceEqual(new[] { rule }, found.ToArray(), $"rule ids found in {markup}");
    }

    /// <summary>
    /// Markup of the allowed forms finds nothing.
    /// </summary>
    /// <param name="markup">The markup</param>
    [DataRow("<script src=\"js/app.js\"></script>")]
    [DataRow("<script src=\"_framework/blazor.web.js\"> </script>")]
    [DataRow("<button @onclick=\"Save\">")]
    [DataRow("<button @onclick:preventDefault=\"true\">")]
    [DataRow("<img src=\"brand/x.svg\">")]
    [DataRow("<a href=\"\">")]
    [DataRow("<base href=\"/\">")]
    [DataRow("<div data-theme-toggle class=\"station\">")]
    [DataRow("<!--Blazor:{\"type\":\"server\",\"descriptor\":\"a/onb=\"}-->")]
    [DataRow("<div @attributes=\"Extra\">")]
    [TestMethod]
    public void WebSourceScannerMarkupAcceptsAllowedForms(string markup)
    {
        // Act
        var found = WebSourceScanner.FindInMarkup(markup);

        // Assert
        Assert.IsEmpty(found, $"rule ids found in {markup}");
    }

    /// <summary>
    /// Component code that passes text as raw markup is found, and only that rule.
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="rule">The only rule id expected</param>
    [DataRow("@((MarkupString)text)", "raw-markup")]
    [DataRow("builder.AddMarkupContent(0, text);", "raw-markup")]
    [DataRow("builder.AddAttribute(1, \"style\", value);", "attribute-name")]
    [DataRow("builder.AddAttribute(1, \"ONCLICK\", value);", "attribute-name")]
    [DataRow("builder.AddAttribute( 1 , \"srcdoc\" , value);", "attribute-name")]
    [TestMethod]
    public void WebSourceScannerComponentCodeFindsRawMarkup(string code, string rule)
    {
        // Act
        var found = WebSourceScanner.FindInComponentCode(code);

        // Assert
        Assert.AreSequenceEqual(new[] { rule }, found.ToArray(), $"rule ids found in {code}");
    }

    /// <summary>
    /// Component code of the allowed forms finds nothing.
    /// </summary>
    /// <param name="code">The code</param>
    [DataRow("builder.AddContent(0, text);")]
    [DataRow("builder.AddAttribute(1, \"class\", value);")]
    [TestMethod]
    public void WebSourceScannerComponentCodeAcceptsAllowedForms(string code)
    {
        // Act
        var found = WebSourceScanner.FindInComponentCode(code);

        // Assert
        Assert.IsEmpty(found, $"rule ids found in {code}");
    }

    /// <summary>
    /// A script that breaks a rule is found, and only that rule.
    /// </summary>
    /// <param name="script">The script</param>
    /// <param name="rule">The only rule id expected</param>
    [DataRow("fetch(\"https://x.example/\");", "double-slash")]
    [DataRow("fetch(\"HTTPS://x.example/\");", "double-slash")]
    [DataRow("fetch(\"//x.example/\");", "double-slash")]
    [DataRow("fetch('//x.example/');", "double-slash")]
    [DataRow("fetch(`//x.example/`);", "double-slash")]
    [DataRow("let a = 1; // note", "double-slash")]
    [DataRow("fetch(\"\\/\\/x.example/\");", "backslash")]
    [DataRow("fetch(\"\\u002f\\u002fx.example/\");", "backslash")]
    [DataRow("eval(x);", "dynamic-code")]
    [DataRow("eval (x);", "dynamic-code")]
    [DataRow("(0,eval)(x);", "dynamic-code")]
    [DataRow("new Function(\"x\");", "dynamic-code")]
    [DataRow("new  Function(\"x\");", "dynamic-code")]
    [DataRow("new\nFunction(\"x\");", "dynamic-code")]
    [DataRow("Function(\"x\")();", "dynamic-code")]
    [DataRow("setTimeout(\"x()\", 1);", "dynamic-code")]
    [DataRow("setTimeout( 'x()' , 1);", "dynamic-code")]
    [DataRow("setInterval(`x()`, 1);", "dynamic-code")]
    [DataRow("el.innerHTML = s;", "html-sink")]
    [DataRow("el.outerHTML = s;", "html-sink")]
    [DataRow("el.insertAdjacentHTML(\"beforeend\", s);", "html-sink")]
    [DataRow("document.write(s);", "html-sink")]
    [DataRow("document.writeln(s);", "html-sink")]
    [DataRow("el.setHTML(s);", "html-sink")]
    [DataRow("el.setHTMLUnsafe(s);", "html-sink")]
    [DataRow("Document.parseHTMLUnsafe(s);", "html-sink")]
    [DataRow("range.createContextualFragment(s);", "html-sink")]
    [DataRow("new DOMParser().parseFromString(s, \"text/html\");", "html-sink")]
    [DataRow("frame.srcdoc = s;", "html-sink")]
    [DataRow("el.setAttribute(\"style\", s);", "set-attribute")]
    [DataRow("el.setAttribute( \"STYLE\" , s);", "set-attribute")]
    [DataRow("el.setAttribute(`onclick`, s);", "set-attribute")]
    [DataRow("el.setAttributeNS(null, \"href\", s);", "set-attribute")]
    [DataRow("const n = parseInt(s, 10);", "global-number-function")]
    [DataRow("const n = parseFloat (s);", "global-number-function")]
    [DataRow("if (isNaN(n)) {}", "global-number-function")]
    [DataRow("if (!isFinite(n)) {}", "global-number-function")]
    [DataRow("const n = window.parseInt(s, 10);", "global-number-function")]
    [DataRow("const n = globalThis.parseFloat(s);", "global-number-function")]
    [DataRow("values.map(parseFloat);", "global-number-function")]
    [DataRow("if (self.isNaN(n)) {}", "global-number-function")]
    [TestMethod]
    public void WebSourceScannerScriptFindsForbiddenForms(string script, string rule)
    {
        // Act
        var found = WebSourceScanner.FindInScript(script);

        // Assert
        if (rule == "backslash")
        {
            Assert.Contains("\\", script, "the input of an escape case still holds its backslash");
        }

        Assert.AreSequenceEqual(new[] { rule }, found.ToArray(), $"rule ids found in {script}");
    }

    /// <summary>
    /// A script of the allowed forms finds nothing.
    /// </summary>
    /// <param name="script">The script</param>
    [DataRow("// a whole-line comment")]
    [DataRow("    // an indented comment")]
    [DataRow("import uPlot from \"../lib/uplot/uPlot.esm.js\";")]
    [DataRow("setTimeout(tick, 1000);")]
    [DataRow("el.style.display = \"none\";")]
    [DataRow("delete document.documentElement.dataset.theme;")]
    [DataRow("el.removeAttribute(\"data-theme\");")]
    [DataRow("function evaluate() { return 1; }")]
    [DataRow("el.textContent = s;")]
    [DataRow("const n = Number.parseInt(s, 16);")]
    [DataRow("const n = Number.parseFloat(s);")]
    [DataRow("if (Number.isNaN(n)) {}")]
    [DataRow("if (Number.isFinite(n)) {}")]
    [DataRow("const n = reader.parseInt(s);")]
    [DataRow("function parseIntoRows(s) { return s; }")]
    [TestMethod]
    public void WebSourceScannerScriptAcceptsAllowedForms(string script)
    {
        // Act
        var found = WebSourceScanner.FindInScript(script);

        // Assert
        Assert.IsEmpty(found, $"rule ids found in {script}");
    }

    /// <summary>
    /// A style sheet that breaks a rule is found, and only that rule.
    /// </summary>
    /// <param name="styleSheet">The style sheet</param>
    /// <param name="ownStyleSheet">Whether the style sheet is an own one</param>
    /// <param name="rule">The only rule id expected</param>
    [DataRow("@import \"x.css\";", false, "import")]
    [DataRow("@IMPORT url(x.css);", false, "import")]
    [DataRow("p{background:url(https://x.example/a.png)}", false, "scheme-url")]
    [DataRow("p{background:URL( \"//x.example/a.png\" )}", false, "protocol-relative-url")]
    [DataRow("p{background:url('//x.example/a.png')}", false, "protocol-relative-url")]
    [DataRow("p{background:url(data:image/png;base64,AA)}", false, "scheme-url")]
    [DataRow("p{background:url(\"\\\\x.example/a.png\")}", false, "protocol-relative-url")]
    [DataRow("p{background:image-set(\"//x.example/a.png\" 1x)}", false, "image-set")]
    [DataRow("p{background:-webkit-image-set(\"a.png\" 1x)}", false, "image-set")]
    [DataRow("@font-face{font-family:x;src:local(x)}", false, "font-face")]
    [DataRow("p::before{content:\"\\2014\"}", true, "backslash")]
    [TestMethod]
    public void WebSourceScannerStyleSheetFindsForbiddenForms(string styleSheet, bool ownStyleSheet, string rule)
    {
        // Act
        var found = WebSourceScanner.FindInStyleSheet(styleSheet, ownStyleSheet);

        // Assert
        if (rule == "backslash")
        {
            Assert.Contains("\\", styleSheet, "the input of an escape case still holds its backslash");
        }

        Assert.AreSequenceEqual(new[] { rule }, found.ToArray(), $"rule ids found in {styleSheet}");
    }

    /// <summary>
    /// A style sheet of the allowed forms finds nothing.
    /// </summary>
    /// <param name="styleSheet">The style sheet</param>
    /// <param name="ownStyleSheet">Whether the style sheet is an own one</param>
    [DataRow("p{background:url(brand/a.svg)}", true)]
    [DataRow("p{color:var(--vandox-color-ink)}", true)]
    [DataRow("p::before{content:\"\\2014\"}", false)]
    [TestMethod]
    public void WebSourceScannerStyleSheetAcceptsAllowedForms(string styleSheet, bool ownStyleSheet)
    {
        // Act
        var found = WebSourceScanner.FindInStyleSheet(styleSheet, ownStyleSheet);

        // Assert
        Assert.IsEmpty(found, $"rule ids found in {styleSheet}");
    }

    #endregion // Methods
}