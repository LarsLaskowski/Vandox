namespace Vandox.Core.LogParsing;

/// <summary>
/// Derives process and program names from the COMMAND column of top.
/// </summary>
internal static class LegacyProcessNames
{
    #region Constants

    private const int MaxCommandBytes = 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Removes the trailing plus of a truncated name.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <param name="truncated">Whether the column ended in a plus</param>
    /// <returns>The normalized text; may be empty</returns>
    internal static string Normalize(string column, out bool truncated)
    {
        truncated = column.EndsWith('+');

        return truncated ? column[..^1] : column;
    }

    /// <summary>
    /// Derives the command of the old format.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <param name="truncated">Whether the column ended in a plus or the command was cut</param>
    /// <returns>The command, never empty and at most 1,024 bytes</returns>
    internal static string OldCommandOf(string column, out bool truncated)
    {
        var normalized = Normalize(column, out truncated);
        var command = Utf8Text.Cut(normalized.Length == 0 ? column : normalized, MaxCommandBytes, out var cut);

        truncated |= cut;

        return command;
    }

    /// <summary>
    /// Derives the command of the new format from the command line.
    /// </summary>
    /// <param name="commandLine">The normalized command line</param>
    /// <param name="column">The COMMAND column as printed</param>
    /// <returns>The command, never empty and at most 1,024 bytes</returns>
    internal static string CommandOf(string commandLine, string column)
    {
        var name = DeriveName(commandLine);

        if (name.Length == 0)
        {
            name = commandLine.Length == 0 ? column : commandLine;
        }

        return Utf8Text.Cut(name, MaxCommandBytes, out _);
    }

    /// <summary>
    /// Derives the program name from a command.
    /// </summary>
    /// <param name="command">The command</param>
    /// <returns>The first word, else the command</returns>
    internal static string ProgramOf(string command)
    {
        var space = command.IndexOf(' ', StringComparison.Ordinal);
        var word = space < 0 ? command : command[..space];

        return word.Length == 0 ? command : word;
    }

    /// <summary>
    /// Derives the name of a process from its command line: the text inside brackets, else the last path part of the first word without a trailing colon.
    /// </summary>
    /// <param name="commandLine">The command line</param>
    /// <returns>The name; may be empty</returns>
    private static string DeriveName(string commandLine)
    {
        if (commandLine.Length >= 2 && commandLine[0] == '[' && commandLine[^1] == ']')
        {
            return commandLine[1..^1];
        }

        var space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        var word = space < 0 ? commandLine : commandLine[..space];

        word = word[(word.LastIndexOf('/') + 1)..];

        return word.EndsWith(':') ? word[..^1] : word;
    }

    #endregion // Methods
}