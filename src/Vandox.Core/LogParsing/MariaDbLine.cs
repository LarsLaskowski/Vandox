namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// The parsed header of an entry of the MariaDB error log.
/// </summary>
internal sealed class MariaDbLine
{
    #region Constants

    /// <summary>
    /// The level <c>ERROR</c>.
    /// </summary>
    internal const string LevelError = "ERROR";

    /// <summary>
    /// The level <c>Warning</c>.
    /// </summary>
    internal const string LevelWarning = "Warning";

    /// <summary>
    /// The level <c>Note</c>.
    /// </summary>
    internal const string LevelNote = "Note";

    /// <summary>
    /// The program of a line written by the start script.
    /// </summary>
    internal const string SafeProgram = "mysqld_safe";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the time: year set (2000 + YY for YYMMDD), no offset, no fraction; digits only, ranges not checked.
    /// </summary>
    internal SyslogTime Time { get; init; }

    /// <summary>
    /// Gets the level, one of the level constants, or empty.
    /// </summary>
    internal string Level { get; init; } = string.Empty;

    /// <summary>
    /// Gets the program: <see cref="SafeProgram"/> for the start script form, else empty.
    /// </summary>
    internal string Program { get; init; } = string.Empty;

    /// <summary>
    /// Gets the rest of the line after the prefix, decoded, not cut.
    /// </summary>
    internal string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the syslog priority: ERROR 3, Warning 4, Note 6, else <c>null</c>.
    /// </summary>
    internal byte? Priority => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Parses the header of an entry; never throws.
    /// </summary>
    /// <param name="line">The raw line</param>
    /// <returns>The header, or <c>null</c> for a continuation line</returns>
    internal static MariaDbLine? TryParse(ReadOnlySpan<byte> line)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}