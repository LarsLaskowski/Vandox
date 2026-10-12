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
    /// The sub-command incidents takes its own config flag and no argument, and its usage names the flag.
    /// </summary>
    [TestMethod]
    public void CommandLineParsesIncidentsSubCommand()
    {
        // Act
        var plain = CommandLine.Parse(["incidents"]);
        var inherited = CommandLine.Parse(["-config", "x", "incidents"]);
        var own = CommandLine.Parse(["incidents", "-config", "x"]);
        var inline = CommandLine.Parse(["incidents", "-config=x"]);
        var help = CommandLine.Parse(["incidents", "-h"]);
        var extra = CommandLine.Parse(["incidents", "extra"]);
        var unknown = CommandLine.Parse(["incidents", "-verbose"]);
        var withHealthcheck = CommandLine.Parse(["-healthcheck", "incidents"]);
        var missing = CommandLine.Parse(["incidents", "-config"]);

        // Assert
        Assert.IsTrue(plain.Incidents, "the sub-command is set");
        Assert.AreEqual("incidents", plain.UsageFor, "usage of the sub-command");
        Assert.AreEqual("/etc/vandox/vandoxd.yaml", plain.ConfigPath, "default config path");
        Assert.AreEqual(string.Empty, plain.Error, "no error");
        Assert.IsNull(plain.ImportPath, "no import");
        Assert.AreEqual("x", inherited.ConfigPath, "the main flag is inherited");
        Assert.IsTrue(inherited.Incidents, "the sub-command after a main flag");
        Assert.AreEqual("x", own.ConfigPath, "the sub-command flag");
        Assert.AreEqual("x", inline.ConfigPath, "the sub-command flag with an equals sign");
        Assert.IsTrue(help.Help, "the help flag");
        Assert.AreEqual("incidents", help.UsageFor, "help shows the usage of the sub-command");
        Assert.AreEqual("vandoxd incidents: no argument is expected", extra.Error, "an argument is refused");
        Assert.AreEqual("flag provided but not defined: -verbose", unknown.Error, "an unknown flag");
        Assert.AreEqual("unexpected argument \"incidents\"", withHealthcheck.Error, "not together with the health check");
        Assert.AreEqual("flag needs an argument: -config", missing.Error, "a config flag without value");
        Assert.AreEqual("Usage of vandoxd incidents [-config file]:\n  -config string\n    \tpath of the configuration file\n", CommandLine.Usage("incidents"), "usage of the sub-command");
        Assert.Contains("  vandoxd [flags] incidents [-config file]\n    \trebuild the incidents from the stored data\n", CommandLine.Usage(string.Empty), "the general usage names the sub-command");
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
        Assert.Contains("-healthcheck", CommandLine.Usage(string.Empty), "usage names the health check flag");
        Assert.Contains("import [-config file] <path>", CommandLine.Usage("import"), "usage of the sub-command");
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