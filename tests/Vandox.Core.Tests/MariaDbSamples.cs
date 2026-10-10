using System.Globalization;
using System.Text;

using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Builds the MariaDB lines the journal and syslog tests feed to the parsers and the groupers: records, journal entries, syslog lines and the feeds of the error log fixture.
/// </summary>
internal static class MariaDbSamples
{
    #region Constants

    /// <summary>
    /// The path of the error log fixture, relative to the repository root.
    /// </summary>
    internal const string FixturePath = "testdata/logs/mariadb-error.log";

    /// <summary>
    /// The host of the lines.
    /// </summary>
    internal const string Host = "web-1";

    /// <summary>
    /// The program of the lines.
    /// </summary>
    internal const string Program = "mariadbd";

    /// <summary>
    /// The prefix of a header in form A with the level <c>Note</c>.
    /// </summary>
    internal const string NotePrefix = "2026-03-01 23:00:05 0 [Note] ";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The time of the first line of most tests: 2026-03-01T22:00:05.123456Z.
    /// </summary>
    internal static readonly DateTimeOffset Start = new DateTimeOffset(2026, 3, 1, 22, 0, 5, TimeSpan.Zero).AddTicks(1234560);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Builds a record of a line as the journal and syslog parsers map it.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="at">The time</param>
    /// <param name="pid">The process ID</param>
    /// <param name="program">The program</param>
    /// <param name="host">The host</param>
    /// <param name="priority">The priority</param>
    /// <param name="truncated">Whether the line was cut</param>
    /// <param name="source">The source type</param>
    /// <param name="log">The log</param>
    /// <returns>The record</returns>
    internal static DataRecord Record(string message, DateTimeOffset at, int pid = 2345, string program = Program, string host = Host, byte? priority = 6, bool truncated = false, string source = "journal", string log = "journal")
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = source,
                   CapturedAt = at,
                   Data = new LogLine
                          {
                              Log = log,
                              Host = host,
                              Program = program,
                              Pid = pid,
                              Priority = priority,
                              Message = message,
                              Truncated = truncated
                          }
               };
    }

    /// <summary>
    /// Builds the message of a header line of the level <c>Note</c>.
    /// </summary>
    /// <param name="text">The text after the prefix</param>
    /// <returns>The message as the journal and syslog parsers map it</returns>
    internal static string Note(string text)
    {
        return NotePrefix + text;
    }

    /// <summary>
    /// Returns the microseconds since the Unix epoch as a <c>__REALTIME_TIMESTAMP</c>.
    /// </summary>
    /// <param name="at">The time</param>
    /// <returns>The decimal text</returns>
    internal static string Micros(DateTimeOffset at)
    {
        return ((at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Adds a journal entry of <c>mariadbd</c> with its own <c>SYSLOG_PID</c>.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="at">The time</param>
    /// <param name="pid">The <c>_PID</c></param>
    /// <param name="syslogPid">The <c>SYSLOG_PID</c></param>
    /// <param name="message">The message</param>
    /// <returns>The builder</returns>
    internal static JournalExportBuilder EntryWithSyslogPid(JournalExportBuilder builder, DateTimeOffset at, int pid, int syslogPid, string message)
    {
        builder.Text("__REALTIME_TIMESTAMP", Micros(at));
        builder.Text("PRIORITY", "6");
        builder.Text("SYSLOG_IDENTIFIER", Program);
        builder.Text("_PID", pid.ToString(CultureInfo.InvariantCulture));
        builder.Text("SYSLOG_PID", syslogPid.ToString(CultureInfo.InvariantCulture));
        builder.Text("_HOSTNAME", Host);
        builder.Text("MESSAGE", message);

        return builder.End();
    }

    /// <summary>
    /// Builds a syslog line with an RFC 3339 time in UTC, without a line feed.
    /// </summary>
    /// <param name="at">The time, to the second</param>
    /// <param name="pid">The process ID</param>
    /// <param name="message">The message; an empty message ends the line after the colon</param>
    /// <param name="program">The program</param>
    /// <param name="prefix">Text before the time, such as <c>&lt;30&gt;</c></param>
    /// <returns>The line</returns>
    internal static string SyslogLine(DateTimeOffset at, int pid, string message, string program = Program, string prefix = "")
    {
        var time = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        return $"{prefix}{time} {Host} {program}[{pid.ToString(CultureInfo.InvariantCulture)}]:{(message.Length == 0 ? string.Empty : " " + message)}";
    }

    /// <summary>
    /// Joins lines into the text of a syslog file: every line ends with a line feed.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The text</returns>
    internal static string SyslogFile(params string[] lines)
    {
        return string.Concat(lines.Select(line => $"{line}\n"));
    }

    /// <summary>
    /// Reads the lines of the error log fixture.
    /// </summary>
    /// <returns>The 57 lines</returns>
    internal static string[] FixtureLines()
    {
        return File.ReadAllLines(RepositoryFiles.Path(FixturePath));
    }

    /// <summary>
    /// Returns the process ID of a line of the fixture in the feeds: 1001 for lines 1 to 6, 2345 for 7 to 10, 3456 for 11 to 52 and 4567 for 53 to 57.
    /// </summary>
    /// <param name="lineNumber">The 1-based line number</param>
    /// <returns>The process ID</returns>
    internal static int FixturePid(int lineNumber)
    {
        return lineNumber switch
               {
                   <= 6 => 1001,
                   <= 10 => 2345,
                   <= 52 => 3456,
                   _ => 4567
               };
    }

    /// <summary>
    /// Returns the time of every line of the fixture: a header has its own time (read as UTC), a continuation line has the time of its header.
    /// </summary>
    /// <param name="lines">The lines of the fixture</param>
    /// <returns>The times, one per line</returns>
    internal static DateTimeOffset[] FixtureTimes(string[] lines)
    {
        var times = new DateTimeOffset[lines.Length];
        var current = default(DateTimeOffset);

        for (var index = 0; index < lines.Length; index++)
        {
            var header = MariaDbLine.TryParse(Encoding.UTF8.GetBytes(lines[index]));

            if (header is not null)
            {
                var time = header.Time;

                current = new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, TimeSpan.Zero);
            }

            times[index] = current;
        }

        return times;
    }

    /// <summary>
    /// Builds the fixture as the bytes of a journal export of <c>mariadbd</c>, priority 6.
    /// </summary>
    /// <param name="dropEmpty"><c>true</c> to leave out the empty lines, as journald stores the output</param>
    /// <returns>The bytes</returns>
    internal static byte[] FixtureJournal(bool dropEmpty)
    {
        var lines = FixtureLines();
        var times = FixtureTimes(lines);
        var builder = new JournalExportBuilder();

        for (var index = 0; index < lines.Length; index++)
        {
            if (dropEmpty && lines[index].Length == 0)
            {
                continue;
            }

            builder.Entry(Micros(times[index]), Host, Program, FixturePid(index + 1), 6, lines[index]);
        }

        return builder.ToArray();
    }

    /// <summary>
    /// Builds the fixture as a syslog file of lines without a priority.
    /// </summary>
    /// <returns>The text</returns>
    internal static string FixtureSyslog()
    {
        var lines = FixtureLines();
        var times = FixtureTimes(lines);
        var text = new StringBuilder();

        for (var index = 0; index < lines.Length; index++)
        {
            text.Append(SyslogLine(times[index], FixturePid(index + 1), lines[index])).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Removes the empty parts of a message that is joined by line feeds.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns>The message without empty lines</returns>
    internal static string WithoutEmptyLines(string message)
    {
        return string.Join('\n', message.Split('\n').Where(part => part.Length > 0));
    }

    #endregion // Methods
}