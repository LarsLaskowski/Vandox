using Vandox.Core.Incidents;

namespace Vandox.Storage;

/// <summary>
/// One row of the incident list.
/// </summary>
public sealed class IncidentSummary
{
    #region Properties

    /// <summary>
    /// Gets or sets the number.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the start of the window.
    /// </summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>
    /// Gets or sets the end of the window.
    /// </summary>
    public DateTimeOffset End { get; set; }

    /// <summary>
    /// Gets or sets the time of the first kill.
    /// </summary>
    public DateTimeOffset FirstKillAt { get; set; }

    /// <summary>
    /// Gets or sets the time of the last kill.
    /// </summary>
    public DateTimeOffset LastKillAt { get; set; }

    /// <summary>
    /// Gets or sets the number of kills.
    /// </summary>
    public long KillCount { get; set; }

    /// <summary>
    /// Gets or sets the name of the first victim.
    /// </summary>
    public string FirstVictim { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hypothesis.
    /// </summary>
    public IncidentHypothesis Hypothesis { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the incident has notes.
    /// </summary>
    public bool HasNotes { get; set; }

    /// <summary>
    /// Gets or sets the time it was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the time it was last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    #endregion // Properties
}