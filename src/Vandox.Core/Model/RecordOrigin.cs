namespace Vandox.Core.Model;

/// <summary>
/// Names of the record origins: who produced a record.
/// </summary>
public static class RecordOrigin
{
    #region Constants

    /// <summary>
    /// Produced by the agent on the monitored server.
    /// </summary>
    public const string Agent = "agent";

    /// <summary>
    /// Produced by the log import.
    /// </summary>
    public const string Import = "import";

    /// <summary>
    /// Produced by the backend itself.
    /// </summary>
    public const string Backend = "backend";

    #endregion // Constants
}