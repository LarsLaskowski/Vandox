using Vandox.Backend.Reports;
using Vandox.Core.Configuration;

namespace Vandox.Backend.Cli;

/// <summary>
/// Parses the command line: <c>vandoxd [flags]</c> and the sub-commands <c>vandoxd [flags] import [-config file] path</c>,
/// <c>incidents</c>, <c>templates</c> and <c>report [-config file] [-lang en|de]</c>. A flag may start with one or two dashes and
/// take its value as the next argument or after an equals sign.
/// </summary>
public static class CommandLine
{
    #region Constants

    /// <summary>
    /// The name of the binary in usage and version output.
    /// </summary>
    public const string BinaryName = "vandoxd";

    private const string ImportName = "import";
    private const string IncidentsName = "incidents";
    private const string TemplatesName = "templates";
    private const string ReportName = "report";
    private const string HashPasswordName = "hash-password";
    private const string ConfigName = "config";
    private const string LanguageName = "lang";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Parses the arguments, without the program name.
    /// </summary>
    /// <param name="args">The arguments</param>
    /// <returns>The options; <see cref="CommandOptions.Error"/> is set when the command line is invalid</returns>
    public static CommandOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandOptions
                      {
                          ConfigPath = ConfigConstants.DefaultBackendFile
                      };
        var index = 0;

        while (index < args.Count && IsFlag(args[index]))
        {
            if (ApplyFlag(options, args, ref index))
            {
                continue;
            }

            return options;
        }

        if (index < args.Count)
        {
            ParseArgument(options, args, index);
        }

        return options;
    }

    /// <summary>
    /// Returns the usage text.
    /// </summary>
    /// <param name="usageFor">The sub-command the usage is for: <c>import</c>, <c>incidents</c>, <c>templates</c>, <c>report</c>, or anything else for the main command</param>
    /// <returns>The text</returns>
    public static string Usage(string usageFor)
    {
        if (usageFor == ImportName)
        {
            return $"Usage of {BinaryName} import [-config file] <path>:\n  <path> is a directory, a .tar or .tar.gz archive, a .gz file or a log file\n  -config string\n    \tpath of the configuration file\n";
        }

        if (usageFor == IncidentsName)
        {
            return $"Usage of {BinaryName} incidents [-config file]:\n  -config string\n    \tpath of the configuration file\n";
        }

        if (usageFor == TemplatesName)
        {
            return $"Usage of {BinaryName} templates [-config file]:\n  -config string\n    \tpath of the configuration file\n";
        }

        if (usageFor == HashPasswordName)
        {
            return $"Usage of {BinaryName} hash-password:\n  reads the password from the first line of standard input and prints its argon2id hash\n";
        }

        if (usageFor == ReportName)
        {
            return $"Usage of {BinaryName} report [-config file] [-lang en|de]:\n  -config string\n    \tpath of the configuration file\n  -lang string\n    \tlanguage of the report: en or de (default \"en\")\n";
        }

        return $"Usage of {BinaryName}:\n  -config string\n    \tpath of the configuration file (default \"{ConfigConstants.DefaultBackendFile}\")\n  -healthcheck\n    \tprobe /healthz of the running service and exit 0 when it is healthy\n  -version\n    \tprint the version and exit\n\nSub-command:\n  {BinaryName} [flags] import [-config file] <path>\n    \timport the log directory, archive or file at <path>\n  {BinaryName} [flags] incidents [-config file]\n    \trebuild the incidents from the stored data\n  {BinaryName} [flags] templates [-config file]\n    \trebuild the log template statistics from the stored log lines\n  {BinaryName} [flags] report [-config file] [-lang en|de]\n    \twrite the forensics report of the stored data as Markdown\n  {BinaryName} hash-password\n    \tread a password from standard input and print its argon2id hash\n";
    }

    /// <summary>
    /// Handles the first argument that is not a flag: the sub-command, or an error.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position of the argument</param>
    private static void ParseArgument(CommandOptions options, IReadOnlyList<string> args, int index)
    {
        if (options.Healthcheck || args[index] is not (ImportName or IncidentsName or TemplatesName or ReportName or HashPasswordName))
        {
            options.Error = $"unexpected argument {Terminal.Quote(args[index])}";
        }
        else if (args[index] == ImportName)
        {
            ParseImport(options, args, index + 1);
        }
        else if (args[index] == HashPasswordName)
        {
            ParseHashPassword(options, args, index + 1);
        }
        else
        {
            ParseStoreCommand(options, args, index + 1, args[index]);
        }
    }

    /// <summary>
    /// Tells whether an argument is a flag: it starts with a dash and is not just a dash.
    /// </summary>
    /// <param name="argument">The argument</param>
    /// <returns><c>true</c> when it is a flag</returns>
    private static bool IsFlag(string argument)
    {
        return argument.Length > 1 && argument[0] == '-' && argument != "--";
    }

    /// <summary>
    /// Applies the flag at <paramref name="index"/> of the main command and advances the index.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position of the flag</param>
    /// <returns><c>true</c> when parsing can go on</returns>
    private static bool ApplyFlag(CommandOptions options, IReadOnlyList<string> args, ref int index)
    {
        var (name, value) = Split(args[index]);

        index++;

        switch (name)
        {
            case "healthcheck":
                {
                    options.Healthcheck = value is null || value == "true";
                }
                break;

            case "version":
                {
                    options.Version = value is null || value == "true";
                }
                break;

            case "h" or "help":
                {
                    options.Help = true;
                }
                break;

            case ConfigName:
                {
                    return ReadConfigValue(options, args, ref index, value);
                }
            default:
                {
                    options.Error = NotDefined(name);

                    return false;
                }
        }

        return true;
    }

    /// <summary>
    /// Reads the value of a <c>-config</c> flag.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position after the flag</param>
    /// <param name="inline">The value after an equals sign, if any</param>
    /// <returns><c>true</c> when the flag has a value</returns>
    private static bool ReadConfigValue(CommandOptions options, IReadOnlyList<string> args, ref int index, string? inline)
    {
        var value = ReadValue(options, args, ref index, inline, ConfigName);

        if (value is not null)
        {
            options.ConfigPath = value;
        }

        return value is not null;
    }

    /// <summary>
    /// Reads the value of a <c>-lang</c> flag: exactly <c>en</c> or <c>de</c>, compared ordinally.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position after the flag</param>
    /// <param name="inline">The value after an equals sign, if any</param>
    /// <returns><c>true</c> when the flag has a valid value</returns>
    private static bool ReadLanguageValue(CommandOptions options, IReadOnlyList<string> args, ref int index, string? inline)
    {
        var value = ReadValue(options, args, ref index, inline, LanguageName);

        if (value is null)
        {
            return false;
        }

        if (ReportText.For(value) is null)
        {
            options.Error = $"{BinaryName} {ReportName}: unknown language {Terminal.Quote(value)} (expected en or de)";

            return false;
        }

        options.Language = value;

        return true;
    }

    /// <summary>
    /// Takes the value of a flag: the text after an equals sign, or the next argument, whatever it looks like.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position after the flag</param>
    /// <param name="inline">The value after an equals sign, if any</param>
    /// <param name="flag">The name of the flag</param>
    /// <returns>The value; <c>null</c> when the flag has none, with the error set</returns>
    private static string? ReadValue(CommandOptions options, IReadOnlyList<string> args, ref int index, string? inline, string flag)
    {
        if (inline is not null)
        {
            return inline;
        }

        if (index < args.Count)
        {
            return args[index++];
        }

        options.Error = $"flag needs an argument: -{flag}";

        return null;
    }

    /// <summary>
    /// Writes the error of a flag that the command does not know. The name is escaped like every text from the command line.
    /// </summary>
    /// <param name="name">The name of the flag, without dashes</param>
    /// <returns>The error text</returns>
    private static string NotDefined(string name)
    {
        var quoted = Terminal.Quote(name);

        return $"flag provided but not defined: -{quoted[1..^1]}";
    }

    /// <summary>
    /// Parses the arguments of the sub-command <c>import</c>.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="start">The position after <c>import</c></param>
    private static void ParseImport(CommandOptions options, IReadOnlyList<string> args, int start)
    {
        var index = start;

        options.UsageFor = ImportName;
        options.ImportPath = string.Empty;

        if (ParseSubFlags(options, args, ref index))
        {
            ImportPathOf(options, args, index);
        }
    }

    /// <summary>
    /// Takes the one path of the sub-command <c>import</c>.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position after the flags</param>
    private static void ImportPathOf(CommandOptions options, IReadOnlyList<string> args, int index)
    {
        if (args.Count - index == 1)
        {
            options.ImportPath = args[index];
        }
        else
        {
            options.Error = $"{BinaryName} import: exactly one path is required";
        }
    }

    /// <summary>
    /// Parses the arguments of the sub-command <c>hash-password</c>: only the help flag, no other flag and no argument.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="start">The position after the sub-command</param>
    private static void ParseHashPassword(CommandOptions options, IReadOnlyList<string> args, int start)
    {
        var index = start;

        options.UsageFor = HashPasswordName;
        options.HashPassword = true;

        if (index < args.Count && IsFlag(args[index]))
        {
            var name = Split(args[index]).Name;

            if (name is "h" or "help")
            {
                options.Help = true;
            }
            else
            {
                options.Error = NotDefined(name);
            }

            return;
        }

        if (index < args.Count)
        {
            options.Error = $"{BinaryName} {HashPasswordName}: no argument is expected";
        }
    }

    /// <summary>
    /// Parses the arguments of the sub-command <c>incidents</c>, <c>templates</c> or <c>report</c>.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="start">The position after the sub-command</param>
    /// <param name="name">The name of the sub-command</param>
    private static void ParseStoreCommand(CommandOptions options, IReadOnlyList<string> args, int start, string name)
    {
        var index = start;

        options.UsageFor = name;
        options.Incidents = name == IncidentsName;
        options.Templates = name == TemplatesName;
        options.Report = name == ReportName;

        if (ParseSubFlags(options, args, ref index) && index < args.Count)
        {
            options.Error = $"{BinaryName} {name}: no argument is expected";
        }
    }

    /// <summary>
    /// Parses the flags of a sub-command: <c>-config</c>, <c>-h</c> and, for <c>report</c> only, <c>-lang</c>.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position of the first flag; receives the position after the flags</param>
    /// <returns><c>false</c> when help was asked for or a flag is wrong</returns>
    private static bool ParseSubFlags(CommandOptions options, IReadOnlyList<string> args, ref int index)
    {
        while (index < args.Count && IsFlag(args[index]))
        {
            var (name, value) = Split(args[index]);

            index++;

            if (name is "h" or "help")
            {
                options.Help = true;

                return false;
            }

            var read = name switch
                       {
                           ConfigName => ReadConfigValue(options, args, ref index, value),
                           LanguageName when options.Report => ReadLanguageValue(options, args, ref index, value),
                           _ => Undefined(options, name)
                       };

            if (read)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Sets the error of a flag that the sub-command does not know.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="name">The name of the flag</param>
    /// <returns>Always <c>false</c></returns>
    private static bool Undefined(CommandOptions options, string name)
    {
        options.Error = NotDefined(name);

        return false;
    }

    /// <summary>
    /// Splits <c>-name=value</c> or <c>--name=value</c> into its name and value.
    /// </summary>
    /// <param name="argument">The flag</param>
    /// <returns>The name and the value; <c>null</c> when there is no equals sign</returns>
    private static (string Name, string? Value) Split(string argument)
    {
        var text = argument.TrimStart('-');
        var equals = text.IndexOf('=', StringComparison.Ordinal);

        return equals < 0 ? (text, null) : (text[..equals], text[(equals + 1)..]);
    }

    #endregion // Methods
}