using System.Globalization;

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
    /// The sub-command templates takes its own config flag and no argument, and its usage names the flag.
    /// </summary>
    [TestMethod]
    public void CommandLineParsesTemplatesSubCommand()
    {
        // Act
        var plain = CommandLine.Parse(["templates"]);
        var inherited = CommandLine.Parse(["-config", "x", "templates"]);
        var own = CommandLine.Parse(["templates", "-config", "x"]);
        var inline = CommandLine.Parse(["templates", "-config=x"]);
        var help = CommandLine.Parse(["templates", "-h"]);
        var extra = CommandLine.Parse(["templates", "extra"]);
        var unknown = CommandLine.Parse(["templates", "-verbose"]);
        var withHealthcheck = CommandLine.Parse(["-healthcheck", "templates"]);
        var missing = CommandLine.Parse(["templates", "-config"]);

        // Assert
        Assert.IsTrue(plain.Templates, "the sub-command is set");
        Assert.IsFalse(plain.Incidents, "not the incidents sub-command");
        Assert.AreEqual("templates", plain.UsageFor, "usage of the sub-command");
        Assert.AreEqual("/etc/vandox/vandoxd.yaml", plain.ConfigPath, "default config path");
        Assert.AreEqual(string.Empty, plain.Error, "no error");
        Assert.IsNull(plain.ImportPath, "no import");
        Assert.AreEqual("x", inherited.ConfigPath, "the main flag is inherited");
        Assert.IsTrue(inherited.Templates, "the sub-command after a main flag");
        Assert.AreEqual("x", own.ConfigPath, "the sub-command flag");
        Assert.AreEqual("x", inline.ConfigPath, "the sub-command flag with an equals sign");
        Assert.IsTrue(help.Help, "the help flag");
        Assert.AreEqual("templates", help.UsageFor, "help shows the usage of the sub-command");
        Assert.AreEqual("vandoxd templates: no argument is expected", extra.Error, "an argument is refused");
        Assert.AreEqual("flag provided but not defined: -verbose", unknown.Error, "an unknown flag");
        Assert.AreEqual("unexpected argument \"templates\"", withHealthcheck.Error, "not together with the health check");
        Assert.AreEqual("flag needs an argument: -config", missing.Error, "a config flag without value");
        Assert.AreEqual("Usage of vandoxd templates [-config file]:\n  -config string\n    \tpath of the configuration file\n", CommandLine.Usage("templates"), "usage of the sub-command");
        Assert.Contains("  vandoxd [flags] templates [-config file]\n    \trebuild the log template statistics from the stored log lines\n", CommandLine.Usage(string.Empty), "the general usage names the sub-command");
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
    /// The name of an unknown flag is escaped in the error, so it cannot inject control or format characters into the terminal.
    /// </summary>
    /// <param name="arguments">The arguments, joined with a bar</param>
    /// <param name="error">The expected error</param>
    [TestMethod]
    [DataRow("-\u202echar", "flag provided but not defined: -\\u202echar")]
    [DataRow("report|--\u202e=x", "flag provided but not defined: -\\u202e")]
    [DataRow("incidents|-a\nb", "flag provided but not defined: -a\\nb")]
    [DataRow("templates|-\u001b[31mred", "flag provided but not defined: -\\u001b[31mred")]
    [DataRow("import|-x\u0085y|a", "flag provided but not defined: -x\\u0085y")]
    public void CommandLineParseEscapesTheNameOfAnUnknownFlag(string arguments, string error)
    {
        // Act
        var options = CommandLine.Parse(arguments.Split('|'));

        // Assert
        Assert.AreEqual(error, options.Error, "error with the escaped name");
        Assert.DoesNotContain(character => char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format, options.Error, "no control or format character in the error");
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
    /// The sub-command report takes its own config and language flags and no argument.
    /// </summary>
    [TestMethod]
    public void CommandLineParsesReportSubCommand()
    {
        // Act
        var plain = CommandLine.Parse(["report"]);
        var inherited = CommandLine.Parse(["-config", "x", "report"]);
        var own = CommandLine.Parse(["report", "-config", "x"]);
        var inline = CommandLine.Parse(["report", "-config=x"]);
        var help = CommandLine.Parse(["report", "-h"]);
        var extra = CommandLine.Parse(["report", "extra"]);
        var unknown = CommandLine.Parse(["report", "-verbose"]);
        var withHealthcheck = CommandLine.Parse(["-healthcheck", "report"]);
        var missing = CommandLine.Parse(["report", "-config"]);
        var missingLanguage = CommandLine.Parse(["report", "-lang"]);
        var german = CommandLine.Parse(["report", "-lang", "de"]);
        var singleDash = CommandLine.Parse(["report", "-lang=de"]);
        var doubleDash = CommandLine.Parse(["report", "--lang", "de"]);
        var doubleDashInline = CommandLine.Parse(["report", "--lang=de"]);
        var tripleDash = CommandLine.Parse(["report", "---lang", "de"]);
        var lastWins = CommandLine.Parse(["report", "-lang", "de", "-lang", "en"]);
        var lastWinsInline = CommandLine.Parse(["report", "-lang=en", "--lang", "de"]);
        var both = CommandLine.Parse(["-config", "main", "report", "-lang", "de", "-config", "own"]);

        // Assert
        Assert.IsTrue(plain.Report, "the sub-command is set");
        Assert.IsFalse(plain.Incidents, "not the incidents sub-command");
        Assert.AreEqual("report", plain.UsageFor, "usage of the sub-command");
        Assert.AreEqual("/etc/vandox/vandoxd.yaml", plain.ConfigPath, "default config path");
        Assert.AreEqual("en", plain.Language, "English by default");
        Assert.AreEqual(string.Empty, plain.Error, "no error");
        Assert.IsNull(plain.ImportPath, "no import");
        Assert.AreEqual("x", inherited.ConfigPath, "the main flag is inherited");
        Assert.IsTrue(inherited.Report, "the sub-command after a main flag");
        Assert.AreEqual("x", own.ConfigPath, "the sub-command flag");
        Assert.AreEqual("x", inline.ConfigPath, "the sub-command flag with an equals sign");
        Assert.IsTrue(help.Help, "the help flag");
        Assert.AreEqual("report", help.UsageFor, "help shows the usage of the sub-command");
        Assert.AreEqual("vandoxd report: no argument is expected", extra.Error, "an argument is refused");
        Assert.AreEqual("flag provided but not defined: -verbose", unknown.Error, "an unknown flag");
        Assert.AreEqual("unexpected argument \"report\"", withHealthcheck.Error, "not together with the health check");
        Assert.AreEqual("flag needs an argument: -config", missing.Error, "a config flag without value");
        Assert.AreEqual("flag needs an argument: -lang", missingLanguage.Error, "a language flag without value");
        Assert.AreEqual("de", german.Language, "-lang de");
        Assert.AreEqual("de", singleDash.Language, "-lang=de");
        Assert.AreEqual("de", doubleDash.Language, "--lang de");
        Assert.AreEqual("de", doubleDashInline.Language, "--lang=de");
        Assert.AreEqual("de", tripleDash.Language, "any number of dashes");
        Assert.AreEqual("en", lastWins.Language, "the last value wins");
        Assert.AreEqual("de", lastWinsInline.Language, "the last value wins also with an equals sign");
        Assert.AreEqual("own", both.ConfigPath, "the config flag of the sub-command wins");
        Assert.AreEqual("de", both.Language, "the language beside the config flag");
        Assert.AreEqual(string.Empty, both.Error, "no error");
    }

    /// <summary>
    /// Any language other than the exact names en and de is refused with the quoted value.
    /// </summary>
    /// <param name="language">The language value</param>
    /// <param name="quoted">The value as the error shows it</param>
    [TestMethod]
    [DataRow("fr", "\"fr\"")]
    [DataRow("EN", "\"EN\"")]
    [DataRow("De", "\"De\"")]
    [DataRow("de-DE", "\"de-DE\"")]
    [DataRow("de_DE", "\"de_DE\"")]
    [DataRow("", "\"\"")]
    [DataRow(" de", "\" de\"")]
    [DataRow("de\n", "\"de\\n\"")]
    [DataRow("-x", "\"-x\"")]
    [DataRow("de=x", "\"de=x\"")]
    [DataRow("\u202e", "\"\\u202e\"")]
    public void CommandLineRefusesReportLanguagesOtherThanEnAndDe(string language, string quoted)
    {
        // Arrange
        var expected = $"vandoxd report: unknown language {quoted} (expected en or de)";

        // Act
        var separate = CommandLine.Parse(["report", "-lang", language]);
        var inline = CommandLine.Parse(["report", $"-lang={language}"]);
        var doubleDash = CommandLine.Parse(["report", "--lang", language]);

        // Assert
        Assert.AreEqual(expected, separate.Error, "-lang value");
        Assert.AreEqual(expected, inline.Error, "-lang=value");
        Assert.AreEqual(expected, doubleDash.Error, "--lang value");

        if (language.Length == 1 && language[0] > 127)
        {
            Assert.AreEqual(UnicodeCategory.Format, CharUnicodeInfo.GetUnicodeCategory(language, 0), "the arranged input is one character of the category Format");
            Assert.AreEqual(6, quoted.Length - 2, "the quoted value shows six ASCII characters between the quotes");
            Assert.IsTrue(quoted.All(character => character < 128), "the error text is ASCII");
        }
    }

    /// <summary>
    /// The language flag is known to the sub-command report only.
    /// </summary>
    [TestMethod]
    public void CommandLineTakesLanguageOnlyForReport()
    {
        // Act
        var beforeReport = CommandLine.Parse(["-lang", "de", "report"]);
        var beforeInline = CommandLine.Parse(["-lang=de", "report"]);
        var main = CommandLine.Parse(["-lang", "de"]);
        var incidents = CommandLine.Parse(["incidents", "-lang", "de"]);
        var templates = CommandLine.Parse(["templates", "-lang=de"]);
        var import = CommandLine.Parse(["import", "-lang", "de", "/logs"]);
        var accepted = CommandLine.Parse(["report", "-lang", "de"]);

        // Assert
        Assert.AreEqual(string.Empty, accepted.Error, "after the sub-command report the flag is known");
        Assert.AreEqual("flag provided but not defined: -lang", beforeReport.Error, "before the sub-command");
        Assert.AreEqual("flag provided but not defined: -lang", beforeInline.Error, "before the sub-command with an equals sign");
        Assert.AreEqual("flag provided but not defined: -lang", main.Error, "for the main command");
        Assert.AreEqual("flag provided but not defined: -lang", incidents.Error, "for incidents");
        Assert.AreEqual("flag provided but not defined: -lang", templates.Error, "for templates");
        Assert.AreEqual("flag provided but not defined: -lang", import.Error, "for import");
        Assert.IsFalse(beforeReport.Report, "the sub-command is not set after the error");
    }

    /// <summary>
    /// The usage of the sub-command report names both flags, and the general usage names the sub-command.
    /// </summary>
    [TestMethod]
    public void CommandLineUsageNamesReportSubCommand()
    {
        // Act
        var usage = CommandLine.Usage("report");
        var general = CommandLine.Usage(string.Empty);

        // Assert
        Assert.AreEqual("Usage of vandoxd report [-config file] [-lang en|de]:\n  -config string\n    \tpath of the configuration file\n  -lang string\n    \tlanguage of the report: en or de (default \"en\")\n", usage, "usage of the sub-command");
        Assert.Contains("  vandoxd [flags] report [-config file] [-lang en|de]\n    \twrite the forensics report of the stored data as Markdown\n", general, "the general usage names the sub-command");
    }

    /// <summary>
    /// The sub-command hash-password takes no flag but the help flag and no argument, and its usage says what it does.
    /// </summary>
    /// <param name="arguments">The arguments, joined with a bar</param>
    /// <param name="hashPassword">The expected sub-command flag</param>
    /// <param name="help">The expected help flag</param>
    /// <param name="error">The expected error</param>
    [TestMethod]
    [DataRow("hash-password", true, false, "")]
    [DataRow("hash-password|-h", true, true, "")]
    [DataRow("hash-password|--help", true, true, "")]
    [DataRow("hash-password|extra", true, false, "vandoxd hash-password: no argument is expected")]
    [DataRow("hash-password|-config|x", true, false, "flag provided but not defined: -config")]
    [DataRow("hash-password|-config=x", true, false, "flag provided but not defined: -config")]
    [DataRow("-healthcheck|hash-password", false, false, "unexpected argument \"hash-password\"")]
    public void CommandLineParsesHashPassword(string arguments, bool hashPassword, bool help, string error)
    {
        // Act
        var options = CommandLine.Parse(arguments.Split('|'));

        // Assert
        Assert.AreEqual(hashPassword, options.HashPassword, "the sub-command is set");
        Assert.AreEqual(help, options.Help, "the help flag");
        Assert.AreEqual(error, options.Error, "error");

        if (hashPassword)
        {
            Assert.AreEqual("hash-password", options.UsageFor, "usage of the sub-command");
        }

        Assert.AreEqual("Usage of vandoxd hash-password:\n  reads the password from the first line of standard input and prints its argon2id hash\n", CommandLine.Usage("hash-password"), "usage of the sub-command");
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