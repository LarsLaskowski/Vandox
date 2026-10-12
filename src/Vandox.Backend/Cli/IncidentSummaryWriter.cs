using System.Globalization;
using System.Text;

using Vandox.Backend.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

/// <summary>
/// Writes the incident lines and the summary of an incident reconstruction as text.
/// </summary>
internal static class IncidentSummaryWriter
{
    #region Methods

    /// <summary>
    /// Formats the line of a saved incident, without a line break.
    /// </summary>
    /// <param name="saved">The saved incident</param>
    /// <returns>The line</returns>
    internal static string FormatIncident(SavedIncident saved)
    {
        var outcome = saved.Result.Outcome switch
                      {
                          IncidentSaveOutcome.Created => "created",
                          IncidentSaveOutcome.Updated => "updated",
                          _ => "unchanged"
                      };
        var victim = saved.Incident.Kills.Count > 0 && saved.Incident.Kills[0].VictimCommand.Length > 0 ? Terminal.Quote(saved.Incident.Kills[0].VictimCommand) : "-";
        var firstKill = saved.Incident.FirstKillAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var line = string.Create(CultureInfo.InvariantCulture, $"#{saved.Result.Id} {outcome} {firstKill} kills={saved.Incident.KillCount} victim={victim}");

        return saved.Result.Merged > 0 ? string.Create(CultureInfo.InvariantCulture, $"{line} merged={saved.Result.Merged}") : line;
    }

    /// <summary>
    /// Writes the counts of a run.
    /// </summary>
    /// <param name="writer">The writer</param>
    /// <param name="result">The result</param>
    /// <returns>A task that completes when the block is written</returns>
    internal static Task WriteAsync(TextWriter writer, ReconstructionResult result)
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"Incidents:           {result.Incidents}\n");
        text.Append(CultureInfo.InvariantCulture, $"  created:           {result.Created}\n");
        text.Append(CultureInfo.InvariantCulture, $"  updated:           {result.Updated}\n");
        text.Append(CultureInfo.InvariantCulture, $"  unchanged:         {result.Unchanged}\n");
        text.Append(CultureInfo.InvariantCulture, $"  merged:            {result.Merged}\n");
        text.Append(CultureInfo.InvariantCulture, $"  kept:              {result.Kept}\n");
        text.Append(CultureInfo.InvariantCulture, $"  skipped:           {result.Skipped}\n");
        text.Append(CultureInfo.InvariantCulture, $"OOM kills:           {result.Kills}\n");
        text.Append(CultureInfo.InvariantCulture, $"Log lines read:      {result.LinesRead}\n");

        return writer.WriteAsync(text.ToString());
    }

    #endregion // Methods
}