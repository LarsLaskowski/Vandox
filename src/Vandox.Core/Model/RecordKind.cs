namespace Vandox.Core.Model;

/// <summary>
/// Names of the record kinds as they appear on the wire and in the database.
/// </summary>
public static class RecordKind
{
    #region Constants

    /// <summary>
    /// A numeric measurement.
    /// </summary>
    public const string Metric = "metric";

    /// <summary>
    /// The process list at one moment.
    /// </summary>
    public const string ProcessSnapshot = "process_snapshot";

    /// <summary>
    /// The socket overview at one moment.
    /// </summary>
    public const string ConnectionSnapshot = "connection_snapshot";

    /// <summary>
    /// The state of one systemd unit.
    /// </summary>
    public const string ServiceState = "service_state";

    /// <summary>
    /// The state of the database at one moment.
    /// </summary>
    public const string MariaDbStatus = "mariadb_status";

    /// <summary>
    /// An event reported by the kernel.
    /// </summary>
    public const string KernelEvent = "kernel_event";

    /// <summary>
    /// One line of a log.
    /// </summary>
    public const string LogLine = "log_line";

    /// <summary>
    /// A period without data.
    /// </summary>
    public const string Gap = "gap";

    #endregion // Constants
}