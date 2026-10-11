using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Classifies the lines of Postfix and Dovecot that the journal and syslog parsers read: the severity marker sets the
/// priority, fatal and panic lines and failed or lost MySQL connections get an event.
/// </summary>
internal static class MailLineClassifier
{
    #region Constants

    private const string PostfixSlash = "postfix/";
    private const string PostfixDash = "postfix-";
    private const string DovecotProgram = "dovecot";
    private const string Separator = ": ";
    private const string AuthWorker = "auth-worker(";
    private const string PostfixHostAnchor = "connect to mysql server ";
    private const string PostfixTableAnchor = "mysql:";
    private const string PostfixQueryFailed = "query failed: ";
    private const string DovecotConnectFailed = "Connect failed to database (";
    private const string DovecotQueryFailed = "Query failed, retrying: ";
    private const string DriverMysql = "mysql: ";
    private const string DriverMysqlOpen = "mysql(";
    private const string DriverSqlPoolOpen = "sqlpool(";
    private const int MaxDriverSegments = 3;
    private const byte PriorityDebug = 7;
    private const byte PriorityWarning = 4;
    private const byte PriorityError = 3;
    private const byte PriorityCritical = 2;
    private const int SeverityOther = 0;
    private const int SeverityFatal = 1;
    private const int SeverityPanic = 2;

    #endregion // Constants

    #region Fields

    private static readonly (string Text, byte Priority, int Severity)[] _postfixMarkers = [
                                                                                               ("warning: ", PriorityWarning, SeverityOther),
                                                                                               ("error: ", PriorityError, SeverityOther),
                                                                                               ("fatal: ", PriorityCritical, SeverityFatal),
                                                                                               ("panic: ", PriorityCritical, SeverityPanic)
                                                                                           ];

    private static readonly (string Text, byte Priority, int Severity)[] _dovecotMarkers = [
                                                                                               ("Debug: ", PriorityDebug, SeverityOther),
                                                                                               ("Warning: ", PriorityWarning, SeverityOther),
                                                                                               ("Error: ", PriorityError, SeverityOther),
                                                                                               ("Fatal: ", PriorityCritical, SeverityFatal),
                                                                                               ("Panic: ", PriorityCritical, SeverityPanic)
                                                                                           ];

    private static readonly string[] _clientErrors = [
                                                         "Can't connect to local MySQL server",
                                                         "Can't connect to MySQL server",
                                                         "MySQL server has gone away",
                                                         "Lost connection to MySQL server",
                                                         "Can't connect to local server",
                                                         "Can't connect to server",
                                                         "Server has gone away",
                                                         "Lost connection to server"
                                                     ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Determines whether a program is a Postfix daemon.
    /// </summary>
    /// <param name="program">The program as the syslog tag or journal identifier gives it</param>
    /// <returns><c>true</c> for <c>postfix/&lt;daemon&gt;</c> and <c>postfix-&lt;name&gt;/&lt;daemon&gt;</c></returns>
    internal static bool IsPostfix(string program)
    {
        if (program.StartsWith(PostfixSlash, StringComparison.Ordinal) || program.StartsWith(PostfixDash, StringComparison.Ordinal))
        {
            var slash = program.LastIndexOf('/');

            return slash >= 0 && slash < program.Length - 1;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a program is Dovecot.
    /// </summary>
    /// <param name="program">The program as the syslog tag or journal identifier gives it</param>
    /// <returns><c>true</c> for <c>dovecot</c> only</returns>
    internal static bool IsDovecot(string program)
    {
        return string.Equals(program, DovecotProgram, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sets priority and event of a Postfix or Dovecot line from its severity marker; other lines stay unchanged.
    /// </summary>
    /// <param name="payload">The line</param>
    internal static void Classify(LogLine payload)
    {
        var postfix = IsPostfix(payload.Program);

        if (postfix || IsDovecot(payload.Program))
        {
            var found = postfix
                            ? FindPostfixMarker(payload.Message, out var marker)
                            : FindDovecotMarker(payload.Message, out marker);

            if (found)
            {
                ApplyMarker(payload, marker, postfix);
            }
        }
    }

    /// <summary>
    /// Applies a found marker to the line.
    /// </summary>
    /// <param name="payload">The line</param>
    /// <param name="marker">The marker found in the message</param>
    /// <param name="postfix"><c>true</c> for a Postfix line, <c>false</c> for a Dovecot line</param>
    private static void ApplyMarker(LogLine payload, (byte Priority, int Severity, int TextStart, int SegmentEnd) marker, bool postfix)
    {
        payload.Priority = marker.Priority;

        if (marker.Severity == SeverityFatal)
        {
            payload.Event = postfix ? MailEvents.PostfixFatal : MailEvents.DovecotFatal;
        }
        else if (marker.Severity == SeverityPanic)
        {
            payload.Event = postfix ? MailEvents.PostfixPanic : MailEvents.DovecotPanic;
        }
        else if (marker.Priority is PriorityWarning or PriorityError)
        {
            var unavailable = postfix
                                  ? IsPostfixDatabaseFailure(payload.Message.AsSpan(marker.TextStart))
                                  : IsDovecotDatabaseFailure(payload.Message, marker);

            if (unavailable)
            {
                payload.Event = postfix ? MailEvents.PostfixDatabaseUnavailable : MailEvents.DovecotDatabaseUnavailable;
            }
        }
    }

    /// <summary>
    /// Finds the Postfix severity marker at the start of a message.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="marker">Receives the marker</param>
    /// <returns><c>true</c> when the message begins with a marker</returns>
    private static bool FindPostfixMarker(string message, out (byte Priority, int Severity, int TextStart, int SegmentEnd) marker)
    {
        return TryMarker(message, 0, -1, _postfixMarkers, out marker);
    }

    /// <summary>
    /// Finds the Dovecot severity marker at the start of a message or after its first segment.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="marker">Receives the marker</param>
    /// <returns><c>true</c> when the message carries a marker at one of the two positions</returns>
    private static bool FindDovecotMarker(string message, out (byte Priority, int Severity, int TextStart, int SegmentEnd) marker)
    {
        if (TryMarker(message, 0, -1, _dovecotMarkers, out marker))
        {
            return true;
        }

        var end = message.IndexOf(Separator, StringComparison.Ordinal);

        if (end > 0 && message.AsSpan(0, end).IndexOf(' ') < 0)
        {
            return TryMarker(message, end + Separator.Length, end, _dovecotMarkers, out marker);
        }

        return false;
    }

    /// <summary>
    /// Finds one of the markers of a family at a position of a message.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="start">The position the marker must begin at</param>
    /// <param name="segmentEnd">The end of the first segment, or -1 when the marker is at the message start</param>
    /// <param name="markers">The markers of the family</param>
    /// <param name="marker">Receives the marker</param>
    /// <returns><c>true</c> when one of the markers is there</returns>
    private static bool TryMarker(string message, int start, int segmentEnd, (string Text, byte Priority, int Severity)[] markers, out (byte Priority, int Severity, int TextStart, int SegmentEnd) marker)
    {
        var rest = message.AsSpan(start);

        foreach (var spec in markers)
        {
            if (rest.StartsWith(spec.Text, StringComparison.Ordinal))
            {
                marker = (spec.Priority, spec.Severity, start + spec.Text.Length, segmentEnd);

                return true;
            }
        }

        marker = default;

        return false;
    }

    /// <summary>
    /// Determines whether the text after a Postfix marker reports a failed or lost MySQL connection.
    /// </summary>
    /// <param name="text">The text after the marker</param>
    /// <returns><c>true</c> for the two anchored forms</returns>
    private static bool IsPostfixDatabaseFailure(ReadOnlySpan<char> text)
    {
        if (text.StartsWith(PostfixHostAnchor, StringComparison.Ordinal))
        {
            return HasNameThenClientError(text[PostfixHostAnchor.Length..], null);
        }

        if (text.StartsWith(PostfixTableAnchor, StringComparison.Ordinal))
        {
            return HasNameThenClientError(text[PostfixTableAnchor.Length..], PostfixQueryFailed);
        }

        return false;
    }

    /// <summary>
    /// Reads a name without space up to the first separator, an optional fixed text and a client error.
    /// </summary>
    /// <param name="text">The text from the name on</param>
    /// <param name="fixedText">The text that must follow the separator, or <c>null</c> for none</param>
    /// <returns><c>true</c> when all parts are there</returns>
    private static bool HasNameThenClientError(ReadOnlySpan<char> text, string? fixedText)
    {
        var end = text.IndexOf(Separator, StringComparison.Ordinal);

        if (end <= 0 || text[..end].IndexOf(' ') >= 0)
        {
            return false;
        }

        var rest = text[(end + Separator.Length)..];

        if (fixedText is not null)
        {
            if (rest.StartsWith(fixedText, StringComparison.Ordinal))
            {
                rest = rest[fixedText.Length..];
            }
            else
            {
                return false;
            }
        }

        return StartsWithClientError(rest);
    }

    /// <summary>
    /// Determines whether the text after a Dovecot marker reports a failed or lost MySQL connection.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="marker">The marker found in it</param>
    /// <returns><c>true</c> for an <c>auth-worker</c> line with the anchored form</returns>
    private static bool IsDovecotDatabaseFailure(string message, (byte Priority, int Severity, int TextStart, int SegmentEnd) marker)
    {
        if (marker.SegmentEnd > 0 && IsAuthWorker(message.AsSpan(0, marker.SegmentEnd)))
        {
            var text = message.AsSpan(marker.TextStart);
            var segments = 0;

            while (segments < MaxDriverSegments && TrySkipDriverSegment(ref text))
            {
                segments++;
            }

            return segments > 0 && IsDovecotClientError(text);
        }

        return false;
    }

    /// <summary>
    /// Determines whether the text after the driver segments is an anchored connection or query failure.
    /// </summary>
    /// <param name="text">The text after the segments</param>
    /// <returns><c>true</c> when it carries a client error at the anchored position</returns>
    private static bool IsDovecotClientError(ReadOnlySpan<char> text)
    {
        if (text.StartsWith(DovecotConnectFailed, StringComparison.Ordinal))
        {
            var close = text.IndexOf("): ", StringComparison.Ordinal);

            return close >= 0 && StartsWithClientError(text[(close + "): ".Length)..]);
        }

        return text.StartsWith(DovecotQueryFailed, StringComparison.Ordinal) && StartsWithClientError(text[DovecotQueryFailed.Length..]);
    }

    /// <summary>
    /// Determines whether a segment is <c>auth-worker(&lt;digits&gt;)</c>.
    /// </summary>
    /// <param name="segment">The first segment of the message</param>
    /// <returns><c>true</c> for the process that holds Dovecot's MySQL connections</returns>
    private static bool IsAuthWorker(ReadOnlySpan<char> segment)
    {
        if (segment.StartsWith(AuthWorker, StringComparison.Ordinal) && segment[^1] == ')')
        {
            var digits = segment[AuthWorker.Length..^1];

            return digits.Length > 0 && digits.IndexOfAnyExceptInRange('0', '9') < 0;
        }

        return false;
    }

    /// <summary>
    /// Skips one driver segment (<c>mysql: </c>, <c>mysql(P): </c> or <c>sqlpool(P): </c>).
    /// </summary>
    /// <param name="text">The text; advanced past the segment when one is there</param>
    /// <returns><c>true</c> when a segment was skipped</returns>
    private static bool TrySkipDriverSegment(ref ReadOnlySpan<char> text)
    {
        if (text.StartsWith(DriverMysql, StringComparison.Ordinal))
        {
            text = text[DriverMysql.Length..];

            return true;
        }

        var open = 0;

        if (text.StartsWith(DriverMysqlOpen, StringComparison.Ordinal))
        {
            open = DriverMysqlOpen.Length;
        }
        else if (text.StartsWith(DriverSqlPoolOpen, StringComparison.Ordinal))
        {
            open = DriverSqlPoolOpen.Length;
        }
        else
        {
            return false;
        }

        var close = text[open..].IndexOf(')');

        if (close <= 0 || text.Slice(open, close).IndexOf(' ') >= 0)
        {
            return false;
        }

        var next = open + close + 1;

        if (text[next..].StartsWith(Separator, StringComparison.Ordinal))
        {
            text = text[(next + Separator.Length)..];

            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a text starts with one of the client errors of an unavailable database.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when it does</returns>
    private static bool StartsWithClientError(ReadOnlySpan<char> text)
    {
        foreach (var error in _clientErrors)
        {
            if (text.StartsWith(error, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    #endregion // Methods
}