using System.Text;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LegacyProcessNames"/>
/// </summary>
[TestClass]
public class LegacyProcessNamesTests
{
    #region Methods

    /// <summary>
    /// A command that ends in a plus sign was cut by top: the plus sign is removed and the cut is reported.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <param name="expected">The expected normalized command</param>
    /// <param name="expectedTruncated">Whether the command was cut</param>
    [TestMethod]
    [DataRow("sw-engi+", "sw-engi", true)]
    [DataRow("apache2", "apache2", false)]
    [DataRow("+", "", true)]
    [DataRow("php-fpm: pool w+", "php-fpm: pool w", true)]
    [DataRow("c++", "c+", true)]
    [DataRow("a+b", "a+b", false)]
    public void LegacyProcessNamesNormalizeRemovesTrailingPlus(string column, string expected, bool expectedTruncated)
    {
        // Act
        var actual = LegacyProcessNames.Normalize(column, out var truncated);

        // Assert
        Assert.AreEqual(expected, actual, "the normalized command");
        Assert.AreEqual(expectedTruncated, truncated, "whether the command was cut");
    }

    /// <summary>
    /// The name of a process is derived from its command line: the text in brackets, or the last path element of the first word without a trailing colon.
    /// </summary>
    /// <param name="commandLine">The command line</param>
    /// <param name="expected">The expected name</param>
    [TestMethod]
    [DataRow("/usr/sbin/apache2 -k start", "apache2")]
    [DataRow("php-fpm: pool www", "php-fpm")]
    [DataRow("[kworker/0:1-events]", "kworker/0:1-events")]
    [DataRow("spamd child", "spamd")]
    [DataRow("/sbin/init splash", "init")]
    [DataRow("sshd: /usr/sbin/sshd -D [listener] 0 of 10-100 startups", "sshd")]
    [DataRow("dovecot/imap-login", "imap-login")]
    [DataRow("/usr/sbin/mariadbd", "mariadbd")]
    public void LegacyProcessNamesCommandOfDerivesNameFromCommandLine(string commandLine, string expected)
    {
        // Act
        var actual = LegacyProcessNames.CommandOf(commandLine, commandLine);

        // Assert
        Assert.AreEqual(expected, actual, "the derived name");
    }

    /// <summary>
    /// When no name can be derived the command line, or the column as printed, is the name: it is never empty.
    /// </summary>
    /// <param name="commandLine">The command line</param>
    /// <param name="column">The COMMAND column as printed</param>
    /// <param name="expected">The expected name</param>
    [TestMethod]
    [DataRow("/opt/x/", "x", "/opt/x/")]
    [DataRow(":", "x", ":")]
    [DataRow("[]", "x", "[]")]
    [DataRow("/", "x", "/")]
    [DataRow("", "+", "+")]
    public void LegacyProcessNamesCommandOfNeverReturnsEmpty(string commandLine, string column, string expected)
    {
        // Act
        var actual = LegacyProcessNames.CommandOf(commandLine, column);

        // Assert
        Assert.AreEqual(expected, actual, "the name is the command line or the column");
    }

    /// <summary>
    /// The name of the old format is the normalized column, or the column as printed when nothing is left.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <param name="expected">The expected name</param>
    /// <param name="expectedTruncated">Whether the command was cut</param>
    [TestMethod]
    [DataRow("+", "+", true)]
    [DataRow("sw-engi+", "sw-engi", true)]
    [DataRow("apache2", "apache2", false)]
    [DataRow("spamd child", "spamd child", false)]
    public void LegacyProcessNamesOldCommandOfNeverReturnsEmpty(string column, string expected, bool expectedTruncated)
    {
        // Act
        var actual = LegacyProcessNames.OldCommandOf(column, out var truncated);

        // Assert
        Assert.AreEqual(expected, actual, "the name");
        Assert.AreEqual(expectedTruncated, truncated, "whether the command was cut");
    }

    /// <summary>
    /// The program of a command is its first word; a command that has none is its own program.
    /// </summary>
    /// <param name="command">The command</param>
    /// <param name="expected">The expected program</param>
    [TestMethod]
    [DataRow("apache2", "apache2")]
    [DataRow("spamd child", "spamd")]
    [DataRow("a b c", "a")]
    [DataRow("kworker/0:1-events", "kworker/0:1-events")]
    [DataRow("apache2 -k", "apache2")]
    public void LegacyProcessNamesProgramOfTakesFirstWord(string command, string expected)
    {
        // Act
        var actual = LegacyProcessNames.ProgramOf(command);

        // Assert
        Assert.AreEqual(expected, actual, "the program");
    }

    /// <summary>
    /// A command whose first word is empty is its own program.
    /// </summary>
    /// <param name="command">The command</param>
    /// <param name="expected">The expected program</param>
    [TestMethod]
    [DataRow(" x", " x")]
    [DataRow("  ", "  ")]
    [DataRow("+", "+")]
    public void LegacyProcessNamesProgramOfNeverReturnsEmpty(string command, string expected)
    {
        // Act
        var actual = LegacyProcessNames.ProgramOf(command);

        // Assert
        Assert.AreEqual(expected, actual, "the program");
        Assert.AreNotEqual(0, actual.Length, "never empty");
    }

    /// <summary>
    /// A name is cut at 1,024 bytes, whichever way it was derived.
    /// </summary>
    [TestMethod]
    public void LegacyProcessNamesCutNamesAt1024Bytes()
    {
        // Arrange
        var longName = new string('a', 3000);

        // Act
        var derived = LegacyProcessNames.CommandOf($"/usr/bin/{longName} -x", "col");
        var fallback = LegacyProcessNames.CommandOf($"/{longName}/", "col");
        var old = LegacyProcessNames.OldCommandOf(longName, out _);

        // Assert
        Assert.AreEqual(1024, Encoding.UTF8.GetByteCount(derived), "the derived name");
        Assert.AreEqual(1024, Encoding.UTF8.GetByteCount(fallback), "the fallback name");
        Assert.AreEqual(1024, Encoding.UTF8.GetByteCount(old), "the old name");
    }

    #endregion // Methods
}