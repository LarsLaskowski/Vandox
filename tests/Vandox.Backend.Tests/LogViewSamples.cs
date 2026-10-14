using System.Globalization;
using System.Text.Encodings.Web;

using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds the stored lines, templates and texts that the tests of the log pages use.
/// </summary>
internal static class LogViewSamples
{
    #region Constants

    /// <summary>
    /// The prefix of the message of an <see cref="InvalidQueryException"/>.
    /// </summary>
    internal const string RulePrefix = "store: invalid query: ";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The capture time of the first sample line.
    /// </summary>
    internal static readonly DateTimeOffset Time = new(2026, 10, 12, 8, 9, 10, 123, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Creates a stored log line.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="message">The message</param>
    /// <param name="priority">The priority, or <c>null</c></param>
    /// <returns>The stored record</returns>
    internal static StoredRecord Line(long id, DateTimeOffset capturedAt, string message, byte? priority = 3)
    {
        return new StoredRecord
               {
                   Id = id,
                   Record = new DataRecord
                            {
                                Origin = RecordOrigin.Backend,
                                Source = "syslog",
                                CapturedAt = capturedAt,
                                Data = new LogLine
                                       {
                                           Log = "auth",
                                           Host = "web-1",
                                           Program = "sshd",
                                           Pid = 1234,
                                           Priority = priority,
                                           Message = message
                                       }
                            }
               };
    }

    /// <summary>
    /// Creates a page of the store.
    /// </summary>
    /// <param name="next">The position after the page, or <c>null</c></param>
    /// <param name="scanLimitReached">Whether the scan bound ended the call</param>
    /// <param name="lines">The lines</param>
    /// <returns>The page</returns>
    internal static LogPage Page(LogPosition? next, bool scanLimitReached, params StoredRecord[] lines)
    {
        return new LogPage
               {
                   Lines = lines,
                   Next = next,
                   ScanLimitReached = scanLimitReached
               };
    }

    /// <summary>
    /// Creates a position.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="id">The record ID</param>
    /// <returns>The position</returns>
    internal static LogPosition Position(DateTimeOffset capturedAt, long id)
    {
        return new LogPosition
               {
                   CapturedAt = capturedAt,
                   Id = id
               };
    }

    /// <summary>
    /// Creates a template.
    /// </summary>
    /// <param name="id">The template ID</param>
    /// <param name="text">The template text</param>
    /// <param name="count">The number of lines over the whole time</param>
    /// <returns>The template</returns>
    internal static LogTemplate Template(long id, string text, long count)
    {
        return new LogTemplate
               {
                   Id = id,
                   Source = "syslog",
                   Program = "sshd",
                   Template = text,
                   FirstSeen = new DateTimeOffset(2026, 9, 20, 6, 5, 4, TimeSpan.Zero),
                   LastSeen = new DateTimeOffset(2026, 10, 12, 8, 9, 10, TimeSpan.Zero),
                   Count = count
               };
    }

    /// <summary>
    /// Creates an entry of the template list.
    /// </summary>
    /// <param name="template">The template</param>
    /// <param name="countInRange">The number of lines in the range</param>
    /// <returns>The entry</returns>
    internal static TemplateCount Counted(LogTemplate template, long countInRange)
    {
        return new TemplateCount
               {
                   Template = template,
                   CountInRange = countInRange
               };
    }

    /// <summary>
    /// Encodes a text the way the framework encodes the text of a page.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The encoded text</returns>
    internal static string Enc(string text)
    {
        return HtmlEncoder.Default.Encode(text);
    }

    /// <summary>
    /// Formats a user text of <see cref="LogsView"/> with one argument.
    /// </summary>
    /// <param name="format">The format</param>
    /// <param name="argument">The argument</param>
    /// <returns>The text</returns>
    internal static string Format(string format, string argument)
    {
        return string.Format(CultureInfo.InvariantCulture, format, argument);
    }

    /// <summary>
    /// Writes a time the way the pages show times.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text in UTC with milliseconds</returns>
    internal static string Shown(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    #endregion // Methods
}