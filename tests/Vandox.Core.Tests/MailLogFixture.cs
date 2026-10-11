using System.Globalization;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Gives the lines of the mail log fixture and what the classification makes of each of them.
/// </summary>
internal static class MailLogFixture
{
    #region Constants

    /// <summary>
    /// The path of the fixture, relative to the repository root.
    /// </summary>
    internal const string RelativePath = "testdata/logs/mail.log";

    /// <summary>
    /// The host of every line.
    /// </summary>
    internal const string Host = "mail-1";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The priority (-1 for none) and the event of each line of the fixture after the classification.
    /// </summary>
    internal static readonly (int Priority, string Event)[] Expected = new (int Priority, string Event)[]
    {
        (-1, string.Empty),
        (-1, string.Empty),
        (4, string.Empty),
        (-1, string.Empty),
        (-1, string.Empty),
        (4, MailEvents.PostfixDatabaseUnavailable),
        (4, MailEvents.PostfixDatabaseUnavailable),
        (2, MailEvents.PostfixFatal),
        (4, string.Empty),
        (2, MailEvents.PostfixPanic),
        (-1, string.Empty),
        (-1, string.Empty),
        (3, MailEvents.DovecotDatabaseUnavailable),
        (4, MailEvents.DovecotDatabaseUnavailable),
        (3, string.Empty),
        (2, MailEvents.DovecotPanic),
        (2, MailEvents.DovecotFatal),
        (-1, string.Empty),
        (4, string.Empty),
        (-1, string.Empty)
    };

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Reads the lines of the fixture.
    /// </summary>
    /// <returns>The 20 lines</returns>
    internal static string[] Lines()
    {
        return File.ReadAllLines(RepositoryFiles.Path(RelativePath));
    }

    /// <summary>
    /// Splits a line of the fixture into program, process ID and message.
    /// </summary>
    /// <param name="line">The line, with the host <c>mail-1</c></param>
    /// <returns>The program, the process ID (0 without one) and the message</returns>
    internal static (string Program, int Pid, string Message) Split(string line)
    {
        var marker = " " + Host + " ";
        var rest = line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        var tagEnd = rest.IndexOf(": ", StringComparison.Ordinal);
        var tag = rest[..tagEnd];
        var message = rest[(tagEnd + 2)..];
        var bracket = tag.IndexOf('[', StringComparison.Ordinal);

        if (bracket < 0)
        {
            return (tag, 0, message);
        }

        return (tag[..bracket], int.Parse(tag[(bracket + 1)..^1], CultureInfo.InvariantCulture), message);
    }

    #endregion // Methods
}