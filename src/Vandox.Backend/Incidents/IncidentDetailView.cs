#pragma warning disable RH2003, S2325

using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// One incident, as its page shows it.
/// </summary>
public sealed class IncidentDetailView
{
    #region Properties

    /// <summary>
    /// Gets the number.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the start of the window.
    /// </summary>
    public DateTimeOffset Start { get; init; }

    /// <summary>
    /// Gets the end of the window.
    /// </summary>
    public DateTimeOffset End { get; init; }

    /// <summary>
    /// Gets the number of kills.
    /// </summary>
    public long KillCount { get; init; }

    /// <summary>
    /// Gets the kills.
    /// </summary>
    public IReadOnlyList<KillRowView> Kills { get; init; } = [];

    /// <summary>
    /// Gets the programs that grew most.
    /// </summary>
    public IReadOnlyList<ProgramAmountView> Growth { get; init; } = [];

    /// <summary>
    /// Gets the text of the growth state.
    /// </summary>
    public string GrowthState { get; init; } = string.Empty;

    /// <summary>
    /// Gets the largest consumers.
    /// </summary>
    public IReadOnlyList<ProgramAmountView> Consumers { get; init; } = [];

    /// <summary>
    /// Gets the text of the backup state.
    /// </summary>
    public string Backup { get; init; } = string.Empty;

    /// <summary>
    /// Gets the timeline in stored order.
    /// </summary>
    public IReadOnlyList<TimelineRowView> Timeline { get; init; } = [];

    /// <summary>
    /// Gets the number of timeline entries the stored incident omits.
    /// </summary>
    public long TimelineOmitted { get; init; }

    /// <summary>
    /// Gets the time of the connection peak, or <c>null</c> without one.
    /// </summary>
    public DateTimeOffset? PeakAt { get; init; }

    /// <summary>
    /// Gets the number of sockets at the peak.
    /// </summary>
    public long PeakSockets { get; init; }

    /// <summary>
    /// Gets the commands with the most sockets at the peak.
    /// </summary>
    public IReadOnlyList<ProgramAmountView> PeakProcesses { get; init; } = [];

    /// <summary>
    /// Gets the metric extremes.
    /// </summary>
    public IReadOnlyList<ExtremeRowView> Extremes { get; init; } = [];

    /// <summary>
    /// Gets the new templates.
    /// </summary>
    public IReadOnlyList<IncidentTemplateView> NewTemplates { get; init; } = [];

    /// <summary>
    /// Gets the template spikes.
    /// </summary>
    public IReadOnlyList<IncidentTemplateView> TemplateSpikes { get; init; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the view of a stored incident.
    /// </summary>
    /// <param name="stored">The stored incident</param>
    /// <returns>The view</returns>
    public static IncidentDetailView From(StoredIncident stored)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}