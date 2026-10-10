using Vandox.Backend.Cli;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="CommandLine"/>, <see cref="Terminal"/> and <see cref="VersionInfo"/>
/// </summary>
[TestClass]
public class CommandLineTests
{
    #region Methods

    /// <summary>
    /// Without arguments the defaults apply.
    /// </summary>
    [TestMethod]
    public void CommandLineParseDefaults()
    {
        // Act
        var options = CommandLine.Parse([]);

        // Assert
        Assert.AreEqual("/etc/vandox/vandoxd.yaml", options.ConfigPath, "default config path");
        Assert.IsFalse(options.Healthcheck, "no health check");
        Assert.IsFalse(options.Version, "no version");
        Assert.IsNull(options.ImportPath, "no import");
        Assert.AreEqual(string.Empty, options.Error, "no error");
    }

    /// <summary>
    /// Flags take one or two dashes and their value as the next argument or after an equals sign.
    /// </summary>
    /// <param name="arguments">The arguments, joined with a bar</param>
    /// <param name="config">The expected config path</param>
    /// <param name="healthcheck">The expected health check flag</param>
    /// <param name="version">The expected version flag</param>
    [TestMethod]
    [DataRow("-config|/a.yaml", "/a.yaml", false, false)]
    [DataRow("--config|/a.yaml", "/a.yaml", false, false)]
    [DataRow("-config=/b.yaml", "/b.yaml", false, false)]
    [DataRow("--config=/b.yaml|-healthcheck", "/b.yaml", true, false)]
    [DataRow("-version", "/etc/vandox/vandoxd.yaml", false, true)]
    [DataRow("--version=true", "/etc/vandox/vandoxd.yaml", false, true)]
    [DataRow("-version=false", "/etc/vandox/vandoxd.yaml", false, false)]
    public void CommandLineParseFlags(string arguments, string config, bool healthcheck, bool version)
    {
        // Act
        var options = CommandLine.Parse(arguments.Split('|'));

        // Assert
        Assert.AreEqual(config, options.ConfigPath, "config path");
        Assert.AreEqual(healthcheck, options.Healthcheck, "health check");
        Assert.AreEqual(version, options.Version, "version");
        Assert.AreEqual(string.Empty, options.Error, "no error");
    }

    /// <summary>
    /// The sub-command import takes its own config flag and exactly one path.
    /// </summary>
    [TestMethod]
    public void CommandLineParseImport()
    {
        // Act
        var plain = CommandLine.Parse(["import", "/logs"]);
        var withConfig = CommandLine.Parse(["-config", "/main.yaml", "import", "-config", "/sub.yaml", "/logs"]);
        var inherited = CommandLine.Parse(["-config", "/main.yaml", "import", "/logs"]);

        // Assert
        Assert.AreEqual("/logs", plain.ImportPath, "path");
        Assert.AreEqual("import", plain.UsageFor, "usage of the sub-command");
        Assert.AreEqual("/sub.yaml", withConfig.ConfigPath, "the sub-command flag wins");
        Assert.AreEqual("/main.yaml", inherited.ConfigPath, "the main flag is inherited");
    }

    /// <summary>
    /// Bad command lines name the problem.
    /// </summary>
    /// <param name="arguments">The arguments, joined with a bar</param>
    /// <param name="error">The expected error</param>
    [TestMethod]
    [DataRow("-nope", "flag provided but not defined: -nope")]
    [DataRow("-config", "flag needs an argument: -config")]
    [DataRow("extra", "unexpected argument \"extra\"")]
    [DataRow("-healthcheck|import|/logs", "unexpected argument \"import\"")]
    [DataRow("import", "vandoxd import: exactly one path is required")]
    [DataRow("import|a|b", "vandoxd import: exactly one path is required")]
    [DataRow("import|-nope|a", "flag provided but not defined: -nope")]
    [DataRow("import|-config", "flag needs an argument: -config")]
    public void CommandLineParseRefusesBadCommandLine(string arguments, string error)
    {
        // Act
        var options = CommandLine.Parse(arguments.Split('|'));

        // Assert
        Assert.AreEqual(error, options.Error, "error");
    }

    /// <summary>
    /// The help flag is recognized for both commands, and the usage texts name the flags.
    /// </summary>
    [TestMethod]
    public void CommandLineHelpAndUsage()
    {
        // Act
        var main = CommandLine.Parse(["-h"]);
        var sub = CommandLine.Parse(["import", "--help"]);

        // Assert
        Assert.IsTrue(main.Help, "help of the main command");
        Assert.IsTrue(sub.Help, "help of the sub-command");
        Assert.Contains("-healthcheck", CommandLine.Usage(false), "usage names the health check flag");
        Assert.Contains("import [-config file] <path>", CommandLine.Usage(true), "usage of the sub-command");
    }

    /// <summary>
    /// Quoting escapes everything that could inject into a terminal.
    /// </summary>
    [TestMethod]
    public void TerminalQuoteEscapesControlAndFormatCharacters()
    {
        // Arrange
        var hostile = string.Concat("a\"b\\c\n\r\t", Chars(0x1b), "[31m", Chars(0x7f, 0x85, 0x200b, 0x2028), "x", Chars(0xfffd, 0xe000), "\u00e4");

        // Act
        var plain = Terminal.Quote("var/log/syslog");
        var escaped = Terminal.Quote(hostile);
        var astral = Terminal.Quote(char.ConvertFromUtf32(0x1F600) + char.ConvertFromUtf32(0xE0001));

        // Assert
        Assert.AreEqual("\"var/log/syslog\"", plain, "plain text");
        Assert.AreEqual("\"a\\\"b\\\\c\\n\\r\\t\\u001b[31m\\u007f\\u0085\\u200b\\u2028x\\ufffd\\ue000\u00e4\"", escaped, "escaped text");
        Assert.AreEqual($"\"{char.ConvertFromUtf32(0x1F600)}\\U000e0001\"", astral, "astral characters");
    }

    /// <summary>
    /// The version line shows version, commit and date of the assembly, with defaults for a local build.
    /// </summary>
    [TestMethod]
    public void VersionInfoDescribesBuild()
    {
        // Act
        var line = VersionInfo.Describe("vandoxd", typeof(Program).Assembly);

        // Assert
        Assert.AreEqual("vandoxd dev (commit unknown, built unknown)", line, "a local build has no injected information");
    }

    /// <summary>
    /// Builds a text from code points.
    /// </summary>
    /// <param name="codePoints">The code points</param>
    /// <returns>The text</returns>
    private static string Chars(params int[] codePoints)
    {
        return string.Concat(codePoints.Select(char.ConvertFromUtf32));
    }

    #endregion // Methods
}