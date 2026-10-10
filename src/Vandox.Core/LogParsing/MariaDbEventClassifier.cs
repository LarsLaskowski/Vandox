namespace Vandox.Core.LogParsing;

/// <summary>
/// Classifies the headers of the MariaDB error log into events and tracks an open crash recovery.
/// </summary>
internal sealed class MariaDbEventClassifier
{
    #region Constants

    private const string StartPrefix = "Starting MariaDB ";
    private const string StartInfix = " as process ";
    private const string OldStartServer = " (server ";
    private const string OldStartProcess = ") starting as process ";
    private const string OldStartSuffix = " ...";
    private const string ReadySuffix = ": ready for connections.";
    private const string ShutdownSuffix = ": Normal shutdown";
    private const string ShutdownCompleteSuffix = ": Shutdown complete";
    private const string SignalInfix = " got signal ";
    private const string SignalSuffix = " ;";
    private const int MaxSignalDigits = 3;
    private const string RecoveryPrefix = "InnoDB: Starting crash recovery";
    private const string TableRecoveryStart = "Starting table crash recovery...";
    private const string TableRecoveryEnd = "Crash table recovery finished.";
    private const string InnoDbPrefix = "InnoDB: ";
    private const string StartedInfix = " started; log sequence number ";

    #endregion // Constants

    #region Fields

    private bool _recoveryOpen;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Classifies a header; the open recovery is tracked in call order.
    /// </summary>
    /// <param name="line">The header</param>
    /// <returns>A value of <see cref="MariaDbEvents"/>, or an empty string for none</returns>
    internal string Classify(MariaDbLine line)
    {
        return line.Level switch
               {
                   MariaDbLine.LevelNote => ClassifyNote(line.Message),
                   MariaDbLine.LevelError => IsAbort(line.Message) ? MariaDbEvents.Abort : string.Empty,
                   _ => string.Empty
               };
    }

    /// <summary>
    /// Tells whether a message is the old or the new wording of the start line.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> for a start line</returns>
    private static bool IsStart(string message)
    {
        if (message.StartsWith(StartPrefix, StringComparison.Ordinal))
        {
            return message.Contains(StartInfix, StringComparison.Ordinal);
        }

        return IsOldStart(message);
    }

    /// <summary>
    /// Tells whether a message is the start line of MariaDB 10.6.7 to 10.6.11.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> for <c>&lt;program&gt; (server &lt;version&gt;) starting as process N ...</c></returns>
    private static bool IsOldStart(string message)
    {
        var server = message.IndexOf(OldStartServer, StringComparison.Ordinal);

        if (server >= 1 && message.EndsWith(OldStartSuffix, StringComparison.Ordinal))
        {
            return message.IndexOf(OldStartProcess, server + OldStartServer.Length, StringComparison.Ordinal) >= 0;
        }

        return false;
    }

    /// <summary>
    /// Tells whether a message is the signal handler's first line.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> for <c>&lt;program&gt; got signal N ;</c></returns>
    private static bool IsAbort(string message)
    {
        if (message.EndsWith(SignalSuffix, StringComparison.Ordinal))
        {
            var body = message.AsSpan(0, message.Length - SignalSuffix.Length);
            var infix = body.LastIndexOf(SignalInfix.AsSpan(), StringComparison.Ordinal);

            return infix >= 1 && IsSignalNumber(body[(infix + SignalInfix.Length)..]);
        }

        return false;
    }

    /// <summary>
    /// Tells whether the text is a signal number of one to three digits.
    /// </summary>
    /// <param name="digits">The text</param>
    /// <returns><c>true</c> for one to three ASCII digits</returns>
    private static bool IsSignalNumber(ReadOnlySpan<char> digits)
    {
        return digits.Length is >= 1 and <= MaxSignalDigits && digits.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>
    /// Tells whether a message is the line that ends the redo recovery of InnoDB.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> for <c>InnoDB: &lt;version&gt; started; log sequence number ...</c></returns>
    private static bool IsInnoDbStarted(string message)
    {
        return message.StartsWith(InnoDbPrefix, StringComparison.Ordinal) && message.Contains(StartedInfix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Classifies the ready, shutdown and shutdown complete lines.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns>The event, or an empty string</returns>
    private static string ClassifyLifecycle(string message)
    {
        if (message.EndsWith(ReadySuffix, StringComparison.Ordinal))
        {
            return MariaDbEvents.Ready;
        }

        if (message.EndsWith(ShutdownSuffix, StringComparison.Ordinal))
        {
            return MariaDbEvents.Shutdown;
        }

        return message.EndsWith(ShutdownCompleteSuffix, StringComparison.Ordinal) ? MariaDbEvents.ShutdownComplete : string.Empty;
    }

    /// <summary>
    /// Classifies the header of a note.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns>The event, or an empty string</returns>
    private string ClassifyNote(string message)
    {
        if (IsStart(message))
        {
            _recoveryOpen = false;

            return MariaDbEvents.Start;
        }

        if (message.StartsWith(RecoveryPrefix, StringComparison.Ordinal) || message == TableRecoveryStart)
        {
            _recoveryOpen = true;

            return MariaDbEvents.RecoveryStart;
        }

        if (message == TableRecoveryEnd || (_recoveryOpen && IsInnoDbStarted(message)))
        {
            _recoveryOpen = false;

            return MariaDbEvents.RecoveryEnd;
        }

        return ClassifyLifecycle(message);
    }

    #endregion // Methods
}