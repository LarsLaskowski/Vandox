using Vandox.Core.Model;

namespace Vandox.Core.Reports;

/// <summary>
/// A listener found by an agent or by comparing after an agent start.
/// </summary>
public sealed class ListenerFinding
{
    #region Properties

    /// <summary>
    /// Gets the origin of the finding.
    /// </summary>
    public ListenerFindingKind Kind { get; init; }

    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the sequence number of the snapshot.
    /// </summary>
    public ulong Seq { get; init; }

    /// <summary>
    /// Gets the capture time of the snapshot.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the capture time of the snapshot compared with, if any.
    /// </summary>
    public DateTimeOffset? BaseCapturedAt { get; init; }

    /// <summary>
    /// Gets the outcome of the comparison.
    /// </summary>
    public ListenerOutcome Outcome { get; init; }

    /// <summary>
    /// Gets the new listeners.
    /// </summary>
    public IReadOnlyList<Listener> Listeners { get; init; } = [];

    /// <summary>
    /// Gets the number of new listeners not listed.
    /// </summary>
    public long Omitted { get; init; }

    #endregion // Properties
}