namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// Derives process and program names from the COMMAND column of top.
/// </summary>
internal static class LegacyProcessNames
{
    #region Methods

    /// <summary>
    /// Removes the trailing plus of a truncated name.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <param name="truncated">Whether the column ended in a plus</param>
    /// <returns>The normalized text; may be empty</returns>
    internal static string Normalize(string column, out bool truncated)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Derives the command of the old format.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <param name="truncated">Whether the column ended in a plus</param>
    /// <returns>The command, never empty and at most 1,024 bytes</returns>
    internal static string OldCommandOf(string column, out bool truncated)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Derives the command of the new format from the command line.
    /// </summary>
    /// <param name="commandLine">The normalized command line</param>
    /// <param name="column">The COMMAND column as printed</param>
    /// <returns>The command, never empty and at most 1,024 bytes</returns>
    internal static string CommandOf(string commandLine, string column)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Derives the program name from a command.
    /// </summary>
    /// <param name="command">The command</param>
    /// <returns>The first word, else the command</returns>
    internal static string ProgramOf(string command)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}