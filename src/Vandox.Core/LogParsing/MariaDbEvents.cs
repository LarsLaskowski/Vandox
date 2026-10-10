namespace Vandox.Core.LogParsing;

/// <summary>
/// The events the MariaDB error log parser recognizes in a line.
/// </summary>
public static class MariaDbEvents
{
    #region Constants

    /// <summary>
    /// The server starts.
    /// </summary>
    public const string Start = "mariadb.start";

    /// <summary>
    /// The server is ready for connections.
    /// </summary>
    public const string Ready = "mariadb.ready";

    /// <summary>
    /// The server begins to shut down.
    /// </summary>
    public const string Shutdown = "mariadb.shutdown";

    /// <summary>
    /// The shutdown is complete.
    /// </summary>
    public const string ShutdownComplete = "mariadb.shutdown_complete";

    /// <summary>
    /// The server aborts.
    /// </summary>
    public const string Abort = "mariadb.abort";

    /// <summary>
    /// A crash recovery starts.
    /// </summary>
    public const string RecoveryStart = "mariadb.recovery_start";

    /// <summary>
    /// A crash recovery ends.
    /// </summary>
    public const string RecoveryEnd = "mariadb.recovery_end";

    #endregion // Constants
}