using System.Text;

using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="WireTextCheck"/>
/// </summary>
[TestClass]
public class WireTextCheckTests
{
    #region Constants

    /// <summary>
    /// The fixed reason for a line that is not valid UTF-8.
    /// </summary>
    private const string NotUtf8 = "line not valid UTF-8";

    /// <summary>
    /// The fixed reason for a line with an unpaired surrogate escape.
    /// </summary>
    private const string UnpairedSurrogate = "unpaired surrogate escape";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Lists the texts the rule accepts.
    /// </summary>
    /// <returns>The data rows: a description and the text</returns>
    public static IEnumerable<object[]> AcceptedTexts()
    {
        return
        [
            ["empty", string.Empty],
            ["ASCII", "plain ASCII text {\"a\":[1,2]}"],
            ["escaped pair", Escape("d83d", "de00")],
            ["escaped pair in upper-case hex", Escape("D83D", "DE00")],
            ["escaped pair in mixed case", Escape("D83d", "De00")],
            ["two pairs", Escape("d83d", "de00", "d83d", "de00")],
            ["replacement character", Escape("fffd")],
            ["noncharacter", Escape("ffff")],
            ["escaped backslash before ud800", @"\\ud800"],
            ["escaped backslash before a pair", $@"\\{Escape("d83d", "de00")}"],
            ["escaped quote", @"\"""],
            ["escaped slash", @"\/"],
            ["escaped b", @"\b"],
            ["escaped f", @"\f"],
            ["escaped n", @"\n"],
            ["escaped r", @"\r"],
            ["escaped t", @"\t"],
            ["non-surrogate escape", Escape("0041", "0042")],
            ["backslash as the last byte", @"a\"],
            ["u with no digits", @"a\u"],
            ["u with two digits", @"a\u12"],
            ["u with three digits", @"a\u123"],
            ["non-hex digits", @"a\uZZZZ"],
            ["whitespace inside an escape", @"\u d800"],
            ["whitespace inside a short escape", @"\u d80"],
            ["sign inside an escape", @"\u+d800"]
        ];
    }

    /// <summary>
    /// Lists the escape forms the rule refuses as an unpaired surrogate escape.
    /// </summary>
    /// <returns>The data rows: a description and the text</returns>
    public static IEnumerable<object[]> UnpairedEscapes()
    {
        return
        [
            ["high alone", Escape("d800")],
            ["low alone", Escape("dc00")],
            ["high then a non-surrogate escape", Escape("d800", "0041")],
            ["high then a letter", $"{Escape("d800")}x"],
            ["low then high", Escape("dc00", "d800")],
            ["high before a valid pair", Escape("d800", "d800", "dc00")],
            ["high in upper-case hex", Escape("D800")],
            ["high before an escaped newline", $@"{Escape("d800")}\n"],
            ["escaped backslash, then a high escape", $@"\\{Escape("d800")}"],
            ["high then an escape with two digits", $@"{Escape("d800")}\u12"],
            ["high then a backslash alone", $@"{Escape("d800")}\"],
            ["high then a non-hex escape", $@"{Escape("d800")}\uZZZZ"],
            ["high then an escape after a space", $"{Escape("d800")} {Escape("dc00")}"],
            ["high then an escape with a space inside", $@"{Escape("d800")}\u dc00"],
            ["high then a high", Escape("d800", "dbff")],
            ["upper edge of the low range alone", Escape("dfff")],
            ["upper edge of the high range alone", Escape("dbff")]
        ];
    }

    /// <summary>
    /// Text with valid UTF-8 and the escape forms the rule keeps gives no violation.
    /// </summary>
    /// <param name="description">The description of the text</param>
    /// <param name="text">The text, as ASCII</param>
    [TestMethod]
    [DynamicData(nameof(AcceptedTexts))]
    public void FindViolationAcceptsValidText(string description, string text)
    {
        // Arrange
        var line = Encoding.ASCII.GetBytes(text);

        // Act
        var violation = WireTextCheck.FindViolation(line);

        // Assert
        Assert.IsNull(violation, $"{description}: no violation");
    }

    /// <summary>
    /// Raw valid multi-byte UTF-8 of one, two, three and four bytes gives no violation.
    /// </summary>
    [TestMethod]
    public void FindViolationAcceptsRawMultiByteText()
    {
        // Arrange
        var line = Encoding.UTF8.GetBytes($"a{char.ConvertFromUtf32(0xE9)}{char.ConvertFromUtf32(0x20AC)}{char.ConvertFromUtf32(0x1F600)}b");

        // Act
        var violation = WireTextCheck.FindViolation(line);

        // Assert
        Assert.IsNull(violation, "valid UTF-8");
    }

    /// <summary>
    /// A byte form that is no valid UTF-8 is a violation in the middle, at the very start and at the very end of the line, and when a letter follows it.
    /// </summary>
    /// <param name="hex">The bytes of the form</param>
    [TestMethod]
    [DataRow("FF")]
    [DataRow("F5808080")]
    [DataRow("80")]
    [DataRow("C0AF")]
    [DataRow("C080")]
    [DataRow("E08080")]
    [DataRow("EDA080")]
    [DataRow("F4908080")]
    [DataRow("E282")]
    [DataRow("F09F98")]
    public void FindViolationRefusesInvalidUtf8(string hex)
    {
        // Arrange
        var form = Convert.FromHexString(hex);
        var prefix = "a"u8.ToArray();
        var suffix = "b"u8.ToArray();

        // Act
        var middle = WireTextCheck.FindViolation([.. prefix, .. form, .. suffix]);
        var start = WireTextCheck.FindViolation([.. form, .. suffix]);
        var end = WireTextCheck.FindViolation([.. prefix, .. form]);
        var whole = WireTextCheck.FindViolation(form);

        // Assert
        Assert.AreEqual(NotUtf8, middle, "in the middle");
        Assert.AreEqual(NotUtf8, start, "at the very start");
        Assert.AreEqual(NotUtf8, end, "at the very end");
        Assert.AreEqual(NotUtf8, whole, "alone");
    }

    /// <summary>
    /// An unpaired surrogate escape is a violation in the middle, at the very start and at the very end of the line.
    /// </summary>
    /// <param name="description">The description of the text</param>
    /// <param name="text">The text, as ASCII</param>
    [TestMethod]
    [DynamicData(nameof(UnpairedEscapes))]
    public void FindViolationRefusesUnpairedSurrogateEscape(string description, string text)
    {
        // Arrange
        Assert.Contains(@"\", text, $"{description}: the input holds a backslash, the escape was kept");
        Assert.Contains(@"\u", text, $"{description}: the input holds a backslash before u, the escape was kept");

        // Act
        var whole = WireTextCheck.FindViolation(Encoding.ASCII.GetBytes(text));
        var middle = WireTextCheck.FindViolation(Encoding.ASCII.GetBytes($"a{text}b"));
        var start = WireTextCheck.FindViolation(Encoding.ASCII.GetBytes($"{text}b"));
        var end = WireTextCheck.FindViolation(Encoding.ASCII.GetBytes($"a{text}"));

        // Assert
        Assert.AreEqual(UnpairedSurrogate, whole, $"{description}: alone");
        Assert.AreEqual(UnpairedSurrogate, middle, $"{description}: in the middle");
        Assert.AreEqual(UnpairedSurrogate, start, $"{description}: at the very start");
        Assert.AreEqual(UnpairedSurrogate, end, $"{description}: at the very end");
    }

    /// <summary>
    /// A line with an invalid byte and an unpaired surrogate escape gives the byte violation, whichever comes first.
    /// </summary>
    [TestMethod]
    public void FindViolationReportsInvalidUtf8BeforeUnpairedEscape()
    {
        // Arrange
        var byteFirst = new byte[] { 0xFF }.Concat(Encoding.ASCII.GetBytes(Escape("d800"))).ToArray();
        var escapeFirst = Encoding.ASCII.GetBytes(Escape("d800")).Concat(new byte[] { 0xFF }).ToArray();

        // Act
        var first = WireTextCheck.FindViolation(byteFirst);
        var second = WireTextCheck.FindViolation(escapeFirst);

        // Assert
        Assert.AreEqual(NotUtf8, first, "byte before escape");
        Assert.AreEqual(NotUtf8, second, "escape before byte");
    }

    /// <summary>
    /// Whitespace inside an escape does not make hex digits: the rule decodes the four digits itself and a space is none.
    /// </summary>
    [TestMethod]
    public void FindViolationReadsNoWhitespaceAsHexDigit()
    {
        // Arrange
        var spaced = Encoding.ASCII.GetBytes(@"\u d800");
        var spacedLow = Encoding.ASCII.GetBytes($@"{Escape("d800")}\u dc00");
        var pairWithSpace = Encoding.ASCII.GetBytes($"{Escape("d800")} {Escape("dc00")}");

        // Act
        var spacedViolation = WireTextCheck.FindViolation(spaced);
        var spacedLowViolation = WireTextCheck.FindViolation(spacedLow);
        var pairViolation = WireTextCheck.FindViolation(pairWithSpace);

        // Assert
        Assert.IsNull(spacedViolation, "a space inside the escape is no hex digit, so no escape of a surrogate stands there");
        Assert.AreEqual(UnpairedSurrogate, spacedLowViolation, "a high half followed by an escape with a space is unpaired");
        Assert.AreEqual(UnpairedSurrogate, pairViolation, "a high half and a low half with a space between them are unpaired");
    }

    /// <summary>
    /// Every short line over an alphabet of the interesting bytes gives a result and never throws.
    /// </summary>
    [TestMethod]
    public void FindViolationNeverThrowsOnShortLines()
    {
        // Arrange
        var alphabet = new byte[] { (byte)'\\', (byte)'u', (byte)'d', (byte)'8', (byte)'c', (byte)'Z', 0xFF, 0xED };
        var line = new byte[7];
        var results = new HashSet<string?>();

        // Act
        for (var length = 0; length <= line.Length; length++)
        {
            var total = (long)Math.Pow(alphabet.Length, length);

            for (long number = 0; number < total; number++)
            {
                var rest = number;

                for (var index = 0; index < length; index++)
                {
                    line[index] = alphabet[(int)(rest % alphabet.Length)];
                    rest /= alphabet.Length;
                }

                results.Add(WireTextCheck.FindViolation(line.AsSpan(0, length)));
            }
        }

        // Assert
        string?[] allowed = [null, NotUtf8, UnpairedSurrogate];

        Assert.IsTrue(results.IsSubsetOf(allowed), "only the three results");
        Assert.HasCount(3, results, "every result occurs");
    }

    /// <summary>
    /// A 1 MiB line of valid text with escaped pairs allocates nothing per call.
    /// </summary>
    [TestMethod]
    public void FindViolationAllocatesNothing()
    {
        // Arrange
        var unit = $"valid text {Escape("d83d", "de00")} and {Escape("00e9")} ";
        var builder = new StringBuilder();

        while (builder.Length < 1024 * 1024)
        {
            builder.Append(unit);
        }

        var line = Encoding.ASCII.GetBytes(builder.ToString());

        Assert.IsNull(WireTextCheck.FindViolation(line), "the line is valid");
        Assert.IsNull(WireTextCheck.FindViolation(line), "the line is valid after warm-up");

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        string? violation = null;

        for (var call = 0; call < 100; call++)
        {
            violation = WireTextCheck.FindViolation(line);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.IsNull(violation, "the line is valid");
        Assert.IsLessThan(1024, allocated, "bytes allocated by 100 calls");
    }

    /// <summary>
    /// Builds the text of <c>\uXXXX</c> escapes, with the backslash written out so it cannot be lost as a C# escape.
    /// </summary>
    /// <param name="units">The four hex digits of each escape</param>
    /// <returns>The text</returns>
    private static string Escape(params string[] units)
    {
        return string.Concat(units.Select(unit => $@"\u{unit}"));
    }

    #endregion // Methods
}