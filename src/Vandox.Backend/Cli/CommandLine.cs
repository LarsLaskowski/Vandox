using Vandox.Core.Configuration;

namespace Vandox.Backend.Cli;

/// <summary>
/// Parses the command line: <c>vandoxd [flags]</c> and <c>vandoxd [flags] import [-config file] path</c>. A flag may start
/// with one or two dashes and take its value as the next argument or after an equals sign.
/// </summary>
public static class CommandLine
{
    #region Constants

    /// <summary>
    /// The name of the binary in usage and version output.
    /// </summary>
    public const string BinaryName = "vandoxd";

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
    /// <param name="forImport">Whether to return the usage of the sub-command</param>
    /// <returns>The text</returns>
    public static string Usage(bool forImport)
    {
        if (forImport)
        {
            return $"Usage of {BinaryName} import [-config file] <path>:\n  <path> is a directory, a .tar or .tar.gz archive, a .gz file or a log file\n  -config string\n    \tpath of the configuration file\n";
        }

        return $"Usage of {BinaryName}:\n  -config string\n    \tpath of the configuration file (default \"{ConfigConstants.DefaultBackendFile}\")\n  -healthcheck\n    \tprobe /healthz of the running service and exit 0 when it is healthy\n  -version\n    \tprint the version and exit\n\nSub-command:\n  {BinaryName} [flags] import [-config file] <path>\n    \timport the log directory, archive or file at <path>\n";
    }

    /// <summary>
    /// Handles the first argument that is not a flag: the sub-command, or an error.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="args">The arguments</param>
    /// <param name="index">The position of the argument</param>
    private static void ParseArgument(CommandOptions options, IReadOnlyList<string> args, int index)
    {
        if (args[index] == "import" && options.Healthcheck)
        {
            options.Error = $"unexpected argument {Terminal.Quote(args[index])}";
        }
        else if (args[index] == "import")
        {
            ParseImport(options, args, index + 1);
        }
        else
        {
            options.Error = $"unexpected argument {Terminal.Quote(args[index])}";
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

            case "config":
                {
                    return ReadConfigValue(options, args, ref index, value);
                }
            default:
                {
                    options.Error = $"flag provided but not defined: -{name}";

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
        if (inline is not null)
        {
            options.ConfigPath = inline;

            return true;
        }

        if (index < args.Count)
        {
            options.ConfigPath = args[index];
            index++;

            return true;
        }

        options.Error = "flag needs an argument: -config";

        return false;
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

        options.UsageFor = "import";
        options.ImportPath = string.Empty;

        while (index < args.Count && IsFlag(args[index]))
        {
            var (name, value) = Split(args[index]);

            index++;

            if (name is "h" or "help")
            {
                options.Help = true;

                return;
            }

            if (name != "config")
            {
                options.Error = $"flag provided but not defined: -{name}";

                return;
            }

            if (ReadConfigValue(options, args, ref index, value))
            {
                continue;
            }

            return;
        }

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