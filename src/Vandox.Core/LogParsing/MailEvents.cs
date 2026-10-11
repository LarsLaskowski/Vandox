namespace Vandox.Core.LogParsing;

/// <summary>
/// The events the classification of mail lines recognizes.
/// </summary>
public static class MailEvents
{
    #region Constants

    /// <summary>
    /// Postfix logs a fatal error.
    /// </summary>
    public const string PostfixFatal = "postfix.fatal";

    /// <summary>
    /// Postfix logs a panic.
    /// </summary>
    public const string PostfixPanic = "postfix.panic";

    /// <summary>
    /// Postfix cannot reach its MySQL or MariaDB database.
    /// </summary>
    public const string PostfixDatabaseUnavailable = "postfix.database_unavailable";

    /// <summary>
    /// Dovecot logs a fatal error.
    /// </summary>
    public const string DovecotFatal = "dovecot.fatal";

    /// <summary>
    /// Dovecot logs a panic.
    /// </summary>
    public const string DovecotPanic = "dovecot.panic";

    /// <summary>
    /// Dovecot cannot reach its MySQL or MariaDB database.
    /// </summary>
    public const string DovecotDatabaseUnavailable = "dovecot.database_unavailable";

    #endregion // Constants
}