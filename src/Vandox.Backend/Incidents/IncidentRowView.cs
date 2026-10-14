using Vandox.Backend.Logs;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// One row of the incident list, as the page shows it.
/// </summary>
public sealed class IncidentRowView
{
    #region Properties

    /// <summary>
    /// Gets the number.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the address of the incident page.
    /// </summary>
    public string Href { get; init; } = string.Empty;

    /// <summary>
    /// Gets the time of the first kill.
    /// </summary>
    public DateTimeOffset FirstKillAt { get; init; }

    /// <summary>
    /// Gets the duration from the first to the last kill as text.
    /// </summary>
    public string Duration { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of kills.
    /// </summary>
    public long KillCount { get; init; }

    /// <summary>
    /// Gets the name of the first victim.
    /// </summary>
    public IReadOnlyList<TextRun> Victim { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether more kills followed the first one.
    /// </summary>
    public bool MoreKills { get; init; }

    /// <summary>
    /// Gets the programs that grew most.
    /// </summary>
    public IReadOnlyList<ProgramAmountView> Growth { get; init; } = [];

    /// <summary>
    /// Gets the text of the growth state: unknown, none or empty when programs are listed.
    /// </summary>
    public string GrowthState { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text of the backup state.
    /// </summary>
    public string Backup { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the incident has notes.
    /// </summary>
    public bool HasNotes { get; init; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the row of a summary.
    /// </summary>
    /// <param name="summary">The summary</param>
    /// <returns>The row</returns>
    public static IncidentRowView From(IncidentSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var hypothesis = summary.Hypothesis;
        var growth = hypothesis.GrowthKnown
                         ? hypothesis.Growth.Select(item => new ProgramAmountView
                                                            {
                                                                Program = LogText.Runs(item.Program, LogText.MaxShortChars),
                                                                Amount = IncidentsView.Growth(item.GrowthBytes)
                                                            })
                                            .ToList()
                         : [];

        return new IncidentRowView
               {
                   Id = summary.Id,
                   Href = IncidentsView.IncidentHref(summary.Id),
                   FirstKillAt = summary.FirstKillAt,
                   Duration = IncidentsView.Duration(summary.LastKillAt - summary.FirstKillAt),
                   KillCount = summary.KillCount,
                   Victim = LogText.Runs(summary.FirstVictim, LogText.MaxShortChars),
                   MoreKills = summary.KillCount > 1,
                   Growth = growth,
                   GrowthState = GrowthText(hypothesis, growth.Count),
                   Backup = BackupText(hypothesis),
                   HasNotes = summary.HasNotes
               };
    }

    /// <summary>
    /// Gets the text of the growth state of a hypothesis.
    /// </summary>
    /// <param name="hypothesis">The hypothesis</param>
    /// <param name="shown">The number of programs shown</param>
    /// <returns>The text; empty when programs are shown</returns>
    internal static string GrowthText(IncidentHypothesis hypothesis, int shown)
    {
        if (hypothesis.GrowthKnown)
        {
            return shown == 0 ? IncidentsView.NoGrowth : string.Empty;
        }

        return IncidentsView.Unknown;
    }

    /// <summary>
    /// Gets the text of the backup state of a hypothesis.
    /// </summary>
    /// <param name="hypothesis">The hypothesis</param>
    /// <returns>The text</returns>
    internal static string BackupText(IncidentHypothesis hypothesis)
    {
        return hypothesis.BackupRunning switch
               {
                   true => IncidentsView.BackupRunning,
                   false => IncidentsView.BackupNotRunning,
                   _ => IncidentsView.Unknown
               };
    }

    #endregion // Methods
}