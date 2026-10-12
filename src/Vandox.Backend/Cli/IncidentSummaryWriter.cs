using Vandox.Backend.Incidents;

namespace Vandox.Backend.Cli;

#pragma warning disable RH2003, S2325

/// <summary>
/// Writes the incident lines and the summary of an incident reconstruction as text.
/// </summary>
internal static class IncidentSummaryWriter
{
    #region Methods

    /// <summary>
    /// Formats the line of a saved incident, without the line break.
    /// </summary>
    /// <param name="saved">The saved incident</param>
    /// <returns>The line</returns>
    internal static string FormatIncident(SavedIncident saved)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the summary.
    /// </summary>
    /// <param name="writer">The writer</param>
    /// <param name="result">The counts</param>
    /// <returns>A task that completes when the summary is written</returns>
    internal static Task WriteAsync(TextWriter writer, ReconstructionResult result)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}