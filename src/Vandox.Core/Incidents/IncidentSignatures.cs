namespace Vandox.Core.Incidents;

/// <summary>
/// The IDs of the built-in signatures incidents are built from.
/// </summary>
public static class IncidentSignatures
{
    #region Constants

    /// <summary>
    /// The OOM kill.
    /// </summary>
    public const string OomKill = "oom_kill";

    /// <summary>
    /// The boot of the machine.
    /// </summary>
    public const string Boot = "boot";

    /// <summary>
    /// The start of a service.
    /// </summary>
    public const string ServiceStart = "service_start";

    /// <summary>
    /// The stop of a service.
    /// </summary>
    public const string ServiceStop = "service_stop";

    /// <summary>
    /// The failure of a service.
    /// </summary>
    public const string ServiceFailed = "service_failed";

    #endregion // Constants
}