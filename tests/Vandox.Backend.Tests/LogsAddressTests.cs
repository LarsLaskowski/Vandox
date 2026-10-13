using System.Globalization;
using System.Text.RegularExpressions;

using Vandox.Backend.Logs;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LogsAddress"/>
/// </summary>
[TestClass]
public partial class LogsAddressTests
{
    #region Fields

    private static readonly DateTimeOffset _from = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset _to = new(2026, 10, 2, 11, 30, 15, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Every parameter is read, with or without the leading question mark.
    /// </summary>
    /// <param name="prefix">The text before the query</param>
    [TestMethod]
    [DataRow("?")]
    [DataRow("")]
    public void ParseReadsEveryParameter(string prefix)
    {
        // Arrange
        var query = prefix + "q=disk+full&source=syslog&program=sshd&severity=warning&template=42&from=2026-10-01T10:00&to=2026-10-02T11:30:15";

        // Act
        var address = LogsAddress.Parse(query);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual("disk full", address.Text, "text");
        Assert.AreEqual("syslog", address.Source, "source");
        Assert.AreEqual("sshd", address.Program, "program");
        Assert.AreEqual((byte)4, address.Severity, "severity warning");
        Assert.AreEqual(42L, address.TemplateId, "template");
        Assert.AreEqual(_from, address.From, "from, read as UTC");
        Assert.AreEqual(_to, address.To, "to, read as UTC with seconds");
        Assert.AreEqual(TimeSpan.Zero, address.From!.Value.Offset, "from has the offset 0");
        Assert.AreEqual(string.Empty, address.Validate(), "the parsed values are valid");
    }

    /// <summary>
    /// The accepted forms of the address table give the stated values.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="expected">The text parameter after parsing</param>
    [TestMethod]
    [DataRow("q=a+b", "a b")]
    [DataRow("q=a%20b", "a b")]
    [DataRow("Q=x", "x")]
    [DataRow("%71=x", "x")]
    [DataRow("q=a=b", "a=b")]
    [DataRow("q=x;y", "x;y")]
    [DataRow("q=%zz", "%zz")]
    [DataRow("q=%", "%")]
    [DataRow("q=%C0%AF", "%C0%AF")]
    [DataRow("q=%ED%A0%80", "%ED%A0%80")]
    [DataRow("q=%F0%9F%98%80", "\U0001f600")]
    [DataRow("q=a%C3%A4b", "aäb")]
    public void ParseReadsTheAcceptedFormsOfTheTable(string query, string expected)
    {
        // Arrange

        // Act
        var address = LogsAddress.Parse(query);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual(expected, address.Text, "the text");
    }

    /// <summary>
    /// A value of exactly 1,024 UTF-8 bytes is valid for q, source and program.
    /// </summary>
    [TestMethod]
    public void ParseAcceptsValuesOfTheLongestLength()
    {
        // Arrange
        var value = new string('a', 1024);
        var twoBytes = new string('ä', 512);

        // Act
        var address = LogsAddress.Parse($"q={value}&source={value}&program={Uri.EscapeDataString(twoBytes)}&template=999999999999999999");

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual(value, address.Text, "text of 1024 bytes");
        Assert.AreEqual(twoBytes, address.Program, "program of 1024 bytes in 512 characters");
        Assert.AreEqual(999999999999999999L, address.TemplateId, "template of 18 digits");
    }

    /// <summary>
    /// Empty values and missing parameters are absent.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("?")]
    [DataRow("q")]
    [DataRow("q=")]
    [DataRow("?q=&source=&program=&severity=&template=&from=&to=")]
    public void ParseTreatsEmptyValuesAsAbsent(string query)
    {
        // Arrange

        // Act
        var address = LogsAddress.Parse(query);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual(string.Empty, address.Text, "text");
        Assert.AreEqual(string.Empty, address.Source, "source");
        Assert.AreEqual(string.Empty, address.Program, "program");
        Assert.IsNull(address.Severity, "severity");
        Assert.AreEqual(0L, address.TemplateId, "template");
        Assert.IsNull(address.From, "from");
        Assert.IsNull(address.To, "to");
        Assert.AreEqual(string.Empty, address.ToQuery(), "nothing to write");
    }

    /// <summary>
    /// Unknown parameters and keys that only look like known ones are ignored.
    /// </summary>
    [TestMethod]
    public void ParseIgnoresUnknownParameters()
    {
        // Arrange
        const string longS = "ſ";
        const string kelvin = "K";
        const string dotless = "ı";

        var query = $"?x=1&page=2&%71%3d=x&;q=x&{longS}ource=x&{kelvin}=x&{dotless}=x&{longS}=x";

        // Act
        var address = LogsAddress.Parse(query);

        // Assert
        Assert.AreEqual(0x017f, char.ConvertToUtf32(longS, 0), "the input holds U+017F, so the escape survived");
        Assert.AreEqual(0x212a, char.ConvertToUtf32(kelvin, 0), "the input holds U+212A, so the escape survived");
        Assert.AreEqual(0x0131, char.ConvertToUtf32(dotless, 0), "the input holds U+0131, so the escape survived");
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual(string.Empty, address.Text, "text");
        Assert.AreEqual(string.Empty, address.Source, "source");
        Assert.AreEqual(string.Empty, address.ToQuery(), "nothing was read");
    }

    /// <summary>
    /// A parameter given more than once is refused, also when some values are empty or the key is written differently.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The parameter named in the error</param>
    [TestMethod]
    [DataRow("q=a&q=b", "q")]
    [DataRow("q=a&Q=b", "q")]
    [DataRow("q=a&%71=b", "q")]
    [DataRow("q=a&q", "q")]
    [DataRow("q=&q=x", "q")]
    [DataRow("source=a&source=b", "source")]
    [DataRow("severity=err&severity=err", "severity")]
    [DataRow("from=2026-10-01T10:00&from=2026-10-01T10:00", "from")]
    public void ParseRefusesRepeatedParameters(string query, string name)
    {
        // Arrange

        // Act
        var address = LogsAddress.Parse(query);

        // Assert
        Assert.AreEqual(Format(LogsView.AddressRepeatedFormat, name), address.Error, "the error names the parameter");
    }

    /// <summary>
    /// A repeated unknown parameter is ignored.
    /// </summary>
    [TestMethod]
    public void ParseIgnoresRepeatedUnknownParameters()
    {
        // Arrange

        // Act
        var address = LogsAddress.Parse("x=1&x=2");

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
    }

    /// <summary>
    /// A value that breaks a rule of the table is refused with the error that names the parameter and never the value.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The parameter named in the error</param>
    [TestMethod]
    [DataRow("q=a%00x", "q")]
    [DataRow("q=a%09b", "q")]
    [DataRow("q=a%0Ab", "q")]
    [DataRow("q=%E2%80%AE", "q")]
    [DataRow("q=a%E2%80%A8b", "q")]
    [DataRow("q=a%E2%80%A9b", "q")]
    [DataRow("source=a%00", "source")]
    [DataRow("source=a%E2%80%AE", "source")]
    [DataRow("program=a%00", "program")]
    [DataRow("program=a%E2%80%AE", "program")]
    [DataRow("severity=ERR", "severity")]
    [DataRow("severity=3", "severity")]
    [DataRow("severity=error", "severity")]
    [DataRow("severity=%20err", "severity")]
    [DataRow("template=0", "template")]
    [DataRow("template=-1", "template")]
    [DataRow("template=%2B5", "template")]
    [DataRow("template=%205", "template")]
    [DataRow("template=abc", "template")]
    [DataRow("template=1234567890123456789", "template")]
    [DataRow("template=%D9%A3", "template")]
    [DataRow("from=2026-10-01", "from")]
    [DataRow("from=2026-10-01T10", "from")]
    [DataRow("from=2026-10-01%2010:00", "from")]
    [DataRow("from=2026-10-01T10:00:00.5", "from")]
    [DataRow("from=2026-10-01T10:00Z", "from")]
    [DataRow("from=2026-10-01T10:00%2B02:00", "from")]
    [DataRow("from=%202026-10-01T10:00", "from")]
    [DataRow("from=2026-13-01T10:00", "from")]
    [DataRow("from=2026-02-30T10:00", "from")]
    [DataRow("from=2026-10-01T10:00:61", "from")]
    [DataRow("from=0001-01-01T00:00", "from")]
    [DataRow("from=9999-12-31T23:59", "from")]
    [DataRow("from=%D9%A2%D9%A0%D9%A2%D9%A6-10-01T10:00", "from")]
    [DataRow("to=2026-10-01", "to")]
    [DataRow("to=2026-10-01T25:00", "to")]
    [DataRow("to=9999-12-31T23:59", "to")]
    public void ParseRefusesInvalidValues(string query, string name)
    {
        // Arrange

        // Act
        var address = LogsAddress.Parse(query);

        // Assert
        Assert.AreEqual(Format(LogsView.AddressInvalidValueFormat, name), address.Error, "the error names the parameter");
    }

    /// <summary>
    /// A value that is too long, a raw lone surrogate and a decoded lone surrogate are refused.
    /// </summary>
    [TestMethod]
    public void ParseRefusesLongValuesAndLoneSurrogates()
    {
        // Arrange
        const string loneSurrogate = "\ud800";

        var rawSurrogate = $"q={loneSurrogate}x";
        var tooLong = new string('a', 1025);
        var tooLongBytes = Uri.EscapeDataString(new string('ä', 513));

        // Act
        var surrogate = LogsAddress.Parse(rawSurrogate);
        var longText = LogsAddress.Parse($"q={tooLong}");
        var longSource = LogsAddress.Parse($"source={tooLong}");
        var longProgram = LogsAddress.Parse($"program={tooLongBytes}");

        // Assert
        Assert.IsTrue(char.IsHighSurrogate(rawSurrogate[2]), "the input holds the lone surrogate, so the escape survived");
        Assert.AreEqual(Format(LogsView.AddressInvalidValueFormat, "q"), surrogate.Error, "a raw lone surrogate is not valid UTF-16");
        Assert.AreEqual(Format(LogsView.AddressInvalidValueFormat, "q"), longText.Error, "q over 1024 bytes");
        Assert.AreEqual(Format(LogsView.AddressInvalidValueFormat, "source"), longSource.Error, "source over 1024 bytes");
        Assert.AreEqual(Format(LogsView.AddressInvalidValueFormat, "program"), longProgram.Error, "program over 1024 bytes in 513 characters");
    }

    /// <summary>
    /// A start that is not before the end is refused.
    /// </summary>
    /// <param name="from">The start</param>
    /// <param name="to">The end</param>
    [TestMethod]
    [DataRow("2026-10-01T10:00", "2026-10-01T10:00")]
    [DataRow("2026-10-02T10:00", "2026-10-01T10:00")]
    [DataRow("2026-10-01T10:00:01", "2026-10-01T10:00")]
    public void ParseRefusesStartNotBeforeEnd(string from, string to)
    {
        // Arrange

        // Act
        var address = LogsAddress.Parse($"from={from}&to={to}");

        // Assert
        Assert.AreEqual(LogsView.AddressRangeReversed, address.Error, "the start is not before the end");
    }

    /// <summary>
    /// Writing the values and reading them again gives the same values, and the query has the stated order and escapes.
    /// </summary>
    [TestMethod]
    public void ToQueryRoundTrips()
    {
        // Arrange
        var address = new LogsAddress
                      {
                          Text = "disk full",
                          Source = "syslog",
                          Program = "sshd",
                          Severity = 4,
                          TemplateId = 42,
                          From = _from,
                          To = _to
                      };
        var minutes = new LogsAddress
                      {
                          Text = "x",
                          From = _from.AddMinutes(5),
                          To = _from.AddHours(2)
                      };

        // Act
        var query = address.ToQuery();
        var again = LogsAddress.Parse(query);
        var minuteQuery = minutes.ToQuery();

        // Assert
        Assert.AreEqual(string.Empty, address.Validate(), "the values are valid");
        Assert.AreEqual("?q=disk%20full&source=syslog&program=sshd&severity=warning&template=42&from=2026-10-01T10%3A00&to=2026-10-02T11%3A30%3A15", query, "order, names and escapes");
        Assert.AreEqual("?q=x&from=2026-10-01T10%3A05&to=2026-10-01T12%3A00", minuteQuery, "seconds are written only when they are not 0");
        Assert.AreEqual(string.Empty, again.Error, "no error");
        Assert.AreEqual(address.Text, again.Text, "text");
        Assert.AreEqual(address.Source, again.Source, "source");
        Assert.AreEqual(address.Program, again.Program, "program");
        Assert.AreEqual(address.Severity, again.Severity, "severity");
        Assert.AreEqual(address.TemplateId, again.TemplateId, "template");
        Assert.AreEqual(address.From, again.From, "from");
        Assert.AreEqual(address.To, again.To, "to");
        Assert.AreEqual(string.Empty, new LogsAddress().ToQuery(), "no filter, no query");
    }

    /// <summary>
    /// Every value is escaped, so no character of the values reaches the query as itself, and the values come back.
    /// </summary>
    [TestMethod]
    public void ToQueryEscapesEveryValue()
    {
        // Arrange
        const string value = "a&b=c#d?e/f%g+h i äöü 日本";

        var address = new LogsAddress
                      {
                          Text = value,
                          Source = value,
                          Program = value
                      };

        // Act
        var query = address.ToQuery();
        var again = LogsAddress.Parse(query);

        // Assert
        var segments = query[1..].Split('&');

        Assert.StartsWith("?q=", query, "q comes first");
        Assert.HasCount(3, segments, "three parameters");

        foreach (var segment in segments)
        {
            var escaped = segment[(segment.IndexOf('=', StringComparison.Ordinal) + 1)..];

            Assert.DoesNotContain(character => character is '&' or '=' or '#' or '?' or '/' or '+' or ' ' || character > 127, escaped, $"no unescaped character in {segment}");
            Assert.DoesNotMatchRegex(BareEscape(), escaped, $"every percent sign starts an escape in {segment}");
        }

        Assert.AreEqual(value, again.Text, "text comes back");
        Assert.AreEqual(value, again.Source, "source comes back");
        Assert.AreEqual(value, again.Program, "program comes back");
    }

    /// <summary>
    /// A value that is not valid is named by Validate, and ToQuery refuses it.
    /// </summary>
    [TestMethod]
    public void ToQueryRefusesAnInvalidValue()
    {
        // Arrange
        const string loneSurrogate = "\ud800";

        var address = new LogsAddress
                      {
                          Text = $"a{loneSurrogate}b"
                      };

        // Act
        var error = address.Validate();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => address.ToQuery());

        // Assert
        Assert.IsTrue(char.IsHighSurrogate(address.Text[1]), "the text holds the lone surrogate, so the escape survived");
        Assert.AreEqual(Format(LogsView.AddressInvalidValueFormat, "q"), error, "Validate names q");
        Assert.IsNotNull(exception, "ToQuery throws");
    }

    /// <summary>
    /// Formats a message of <see cref="LogsView"/> with one argument.
    /// </summary>
    /// <param name="format">The format</param>
    /// <param name="argument">The argument</param>
    /// <returns>The text</returns>
    private static string Format(string format, string argument)
    {
        return string.Format(CultureInfo.InvariantCulture, format, argument);
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("%(?![0-9A-F]{2})")]
    private static partial Regex BareEscape();

    #endregion // Patterns
}