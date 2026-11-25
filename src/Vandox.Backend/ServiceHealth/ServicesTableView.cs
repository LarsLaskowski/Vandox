using System.Globalization;

using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.Overview;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// Builds the tables of the section Services from the availability and the restarts.
/// </summary>
public static class ServicesTableView
{
    #region Constants

    private const string NoRestartText = "none";

    private const string UnknownText = "unknown";

    #endregion // Constants

    #region Fields

    private static readonly string[] _unitColumns = ["Unit", "Available", "Not available", "Unknown", "Restarts", "Last restart", "Latest state"];

    private static readonly string[] _restartColumns = ["Time", "Unit"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Builds the tables and notes of the section.
    /// </summary>
    /// <param name="availability">The availability of the units</param>
    /// <param name="restarts">The restarts of the range</param>
    /// <param name="range">The range, its end already clamped to now</param>
    /// <returns>The section</returns>
    public static ServicesTable Build(ServiceAvailabilityResult availability, SeriesMarkerList restarts, HistoryRange range)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(restarts);
        ArgumentNullException.ThrowIfNull(range);

        var perUnit = restarts.Markers.Where(marker => marker.Kind == MarkerKind.ServiceRestart).GroupBy(marker => marker.Text, StringComparer.Ordinal).ToDictionary(group => group.Key, group => (Count: group.Count(), Last: group.Max(marker => marker.At)), StringComparer.Ordinal);
        var units = availability.Units.OrderByDescending(unit => Restarts(perUnit, unit.Unit))
                                      .ThenByDescending(unit => unit.NotAvailable)
                                      .ThenBy(unit => unit.Unit, StringComparer.Ordinal)
                                      .ToList();
        var more = availability.UnitCount - availability.Units.Count;

        return new ServicesTable
               {
                   Units = new SnapshotTableView
                           {
                               Heading = ServicesView.ServicesHeading,
                               Columns = _unitColumns,
                               Rows = [.. units.Select(unit => UnitRow(unit, perUnit))],
                               More = more > 0 ? HistoryView.Fill(ServicesView.MoreUnitsFormat, more.ToString(CultureInfo.InvariantCulture)) : string.Empty
                           },
                   Restarts = new SnapshotTableView
                              {
                                  Heading = ServicesView.RestartsHeading,
                                  Columns = _restartColumns,
                                  Rows = [.. restarts.Markers.Where(marker => marker.Kind == MarkerKind.ServiceRestart).OrderBy(marker => marker.At).Take(TimeSeriesService.MaxMarkers).Select(marker => SnapshotCells.Row(SnapshotCells.Plain(ServicesView.FormatTime(marker.At)), SnapshotCells.Runs(marker.Text)))]
                              },
                   Notes = Notes(availability, restarts),
                   Empty = availability.Units.Count == 0
               };
    }

    /// <summary>
    /// Gives the number of restarts of a unit.
    /// </summary>
    /// <param name="perUnit">The restarts per unit</param>
    /// <param name="unit">The unit</param>
    /// <returns>The number</returns>
    private static int Restarts(Dictionary<string, (int Count, DateTimeOffset Last)> perUnit, string unit)
    {
        return perUnit.TryGetValue(unit, out var found) ? found.Count : 0;
    }

    /// <summary>
    /// Builds the row of a unit.
    /// </summary>
    /// <param name="unit">The unit</param>
    /// <param name="perUnit">The restarts per unit</param>
    /// <returns>The row</returns>
    private static SnapshotRowView UnitRow(ServiceAvailability unit, Dictionary<string, (int Count, DateTimeOffset Last)> perUnit)
    {
        var known = unit.Available + unit.NotAvailable;
        var restarted = perUnit.TryGetValue(unit.Unit, out var found);

        return SnapshotCells.Row(SnapshotCells.Runs(unit.Unit),
                                 SnapshotCells.Plain(known <= TimeSpan.Zero ? UnknownText : ServicesView.ShareDown(unit.Available, known)),
                                 SnapshotCells.Plain(OverviewText.Age(unit.NotAvailable)),
                                 SnapshotCells.Plain(OverviewText.Age(unit.Unknown)),
                                 SnapshotCells.Plain(found.Count),
                                 SnapshotCells.Plain(restarted ? ServicesView.FormatTime(found.Last) : NoRestartText),
                                 LatestState(unit));
    }

    /// <summary>
    /// Builds the cell of the latest state of a unit: the states and the time of the record.
    /// </summary>
    /// <param name="unit">The unit</param>
    /// <returns>The runs</returns>
    private static IReadOnlyList<TextRun> LatestState(ServiceAvailability unit)
    {
        if (unit.StateAt is null)
        {
            return SnapshotCells.Plain(UnknownText);
        }

        return [.. SnapshotCells.Runs($"{unit.ActiveState} ({unit.SubState})"), new TextRun($", {ServicesView.FormatTime(unit.StateAt.Value)} UTC", false)];
    }

    /// <summary>
    /// Builds the notes in the order of the edge cases: the gaps of the systemd collector, the gaps not read, the restarts not counted.
    /// </summary>
    /// <param name="availability">The availability</param>
    /// <param name="restarts">The restarts</param>
    /// <returns>The notes</returns>
    private static List<string> Notes(ServiceAvailabilityResult availability, SeriesMarkerList restarts)
    {
        var notes = new List<string>();

        if (availability.SystemdGaps)
        {
            notes.Add(ServicesView.SystemdGapsText);
        }

        if (availability.GapsCompleteUntil is { } gaps)
        {
            notes.Add(HistoryView.Fill(ServicesView.GapsNotReadFormat, ServicesView.FormatTime(gaps)));
        }

        if (restarts.CompleteUntil is { } restartsUntil)
        {
            notes.Add(HistoryView.Fill(ServicesView.RestartsNotCountedFormat, ServicesView.FormatTime(restartsUntil)));
        }

        return notes;
    }

    #endregion // Methods
}