using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Reads the markers of a range: OOM kills, boots, service restarts and backup runs, as the plan and records 0215 and 0216 describe.
/// </summary>
internal static class SeriesMarkerReader
{
    #region Constants

    private static readonly string[] _runningStates = ["active", "activating", "reloading", "deactivating", "failed"];

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Reads the markers of the range, at most <see cref="TimeSeriesService.MaxMarkers"/>.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the markers in time order and the time up to which the list is complete, <c>null</c> when it is complete</returns>
    internal static async Task<(List<SeriesMarker> Markers, DateTimeOffset? CompleteUntil)> ReadAsync(ITimeSeriesReader reader, SeriesRange range, CancellationToken cancellationToken)
    {
        var items = new List<(SeriesMarker Marker, long Id)>();
        var bound = await AddKernelAsync(reader, range, items, cancellationToken).ConfigureAwait(false);

        bound = SeriesTime.Earlier(bound, await AddRestartsAsync(reader, range, items, cancellationToken).ConfigureAwait(false));
        bound = SeriesTime.Earlier(bound, await AddBackupsAsync(reader, range, items, cancellationToken).ConfigureAwait(false));

        var sorted = items.OrderBy(item => item.Marker.At).ThenBy(item => item.Marker.Kind).ThenBy(item => item.Id).Select(item => item.Marker).ToList();

        if (sorted.Count > TimeSeriesService.MaxMarkers)
        {
            bound = SeriesTime.Earlier(bound, sorted[TimeSeriesService.MaxMarkers].At);
            sorted = sorted.Take(TimeSeriesService.MaxMarkers).ToList();
        }

        return (sorted, bound);
    }

    /// <summary>
    /// Adds the OOM kills, once per boot ID, kernel time and process ID, and the boots, once per boot ID.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="items">The markers; the new ones are added</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the time up to which the kernel events are complete, or <c>null</c></returns>
    private static async Task<DateTimeOffset?> AddKernelAsync(ITimeSeriesReader reader, SeriesRange range, List<(SeriesMarker Marker, long Id)> items, CancellationToken cancellationToken)
    {
        var read = await EventPages.ReadAsync(query => reader.KernelEventsAsync(query, cancellationToken), range.From, range.End, row => row.CapturedAt, row => row.Id, cancellationToken).ConfigureAwait(false);
        var kills = new HashSet<(string, long, long)>();
        var boots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in read.Rows)
        {
            var marker = row.Type switch
                         {
                             KernelEvent.TypeOomKill => KillMarker(row, kills),
                             KernelEvent.TypeBoot => BootMarker(row, boots),
                             _ => null
                         };

            if (marker is not null)
            {
                items.Add((marker, row.Id));
            }
        }

        return read.RanOutAt;
    }

    /// <summary>
    /// Builds the marker of an OOM kill unless the same kill was seen before.
    /// </summary>
    /// <param name="row">The kernel event</param>
    /// <param name="seen">The identities seen</param>
    /// <returns>The marker, or <c>null</c> for a kill seen before</returns>
    private static SeriesMarker? KillMarker(KernelEventRow row, HashSet<(string, long, long)> seen)
    {
        if (row.BootId.Length > 0 && row.KernelTimeNs is not null && row.VictimPid is not null && seen.Add((row.BootId, row.KernelTimeNs.Value, row.VictimPid.Value)) == false)
        {
            return null;
        }

        return new SeriesMarker
               {
                   Kind = MarkerKind.OomKill,
                   At = row.CapturedAt,
                   Text = row.VictimCommand,
                   Pid = row.VictimPid,
                   BootId = row.BootId
               };
    }

    /// <summary>
    /// Builds the marker of a boot unless its boot ID was seen before.
    /// </summary>
    /// <param name="row">The kernel event</param>
    /// <param name="seen">The boot IDs seen</param>
    /// <returns>The marker, or <c>null</c> for a boot seen before</returns>
    private static SeriesMarker? BootMarker(KernelEventRow row, HashSet<string> seen)
    {
        if (row.BootId.Length > 0 && seen.Add(row.BootId) == false)
        {
            return null;
        }

        return new SeriesMarker
               {
                   Kind = MarkerKind.Boot,
                   At = row.CapturedAt,
                   BootId = row.BootId
               };
    }

    /// <summary>
    /// Adds the service restarts of the range.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="items">The markers; the new ones are added</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the time up to which the restarts are complete, or <c>null</c></returns>
    private static async Task<DateTimeOffset?> AddRestartsAsync(ITimeSeriesReader reader, SeriesRange range, List<(SeriesMarker Marker, long Id)> items, CancellationToken cancellationToken)
    {
        var read = await reader.ServiceChangesAsync(new TimeRangeQuery
                                                    {
                                                        From = range.From,
                                                        To = range.End,
                                                        Limit = TimeSeriesQueries.MaxServiceLimit
                                                    },
                                                    cancellationToken).ConfigureAwait(false);
        var full = read.Count >= TimeSeriesQueries.MaxServiceLimit;
        var rows = read.Take(TimeSeriesQueries.MaxServiceLimit - 1).ToList();
        DateTimeOffset? bound = full ? read[TimeSeriesQueries.MaxServiceLimit - 1].CapturedAt : null;

        var previous = await PreviousAsync(reader, rows, range.From, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var before = row.Previous ?? previous.GetValueOrDefault(row.Unit);

            if (before is not null && await IsRestartAsync(reader, row, before, cancellationToken).ConfigureAwait(false))
            {
                items.Add((RestartMarker(row, before, range), 0));
            }
        }

        return bound;
    }

    /// <summary>
    /// Reads the previous record before the range of every unit whose first record in the range has none.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="rows">The service rows</param>
    /// <param name="from">The start of the range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the previous record of each unit that has one</returns>
    private static async Task<Dictionary<string, ServiceStateRow>> PreviousAsync(ITimeSeriesReader reader, IReadOnlyList<ServiceStateRow> rows, DateTimeOffset from, CancellationToken cancellationToken)
    {
        var units = rows.Where(row => row.Previous is null).Select(row => row.Unit).Distinct(StringComparer.Ordinal).ToList();

        if (units.Count == 0)
        {
            return [];
        }

        var found = await reader.PreviousServiceStatesAsync(units, from, cancellationToken).ConfigureAwait(false);

        return found.GroupBy(row => row.Unit, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Tells whether a service record is a restart: its counter rose, or its enter time moved while the unit ran or had failed and no boot lies between.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="row">The record</param>
    /// <param name="before">The unit's previous record</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <c>true</c> for a restart</returns>
    private static async Task<bool> IsRestartAsync(ITimeSeriesReader reader, ServiceStateRow row, ServiceStateRow before, CancellationToken cancellationToken)
    {
        if (row.Restarts > before.Restarts)
        {
            return true;
        }

        return EnterMoved(row, before) is not null && _runningStates.Contains(before.ActiveState) && await NoBootBetweenAsync(reader, row, before, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the new enter time of a record when it is later than, or newly present against, its predecessor's.
    /// </summary>
    /// <param name="row">The record</param>
    /// <param name="before">The predecessor</param>
    /// <returns>The new enter time, or <c>null</c> when it did not move or a time does not parse</returns>
    private static DateTimeOffset? EnterMoved(ServiceStateRow row, ServiceStateRow before)
    {
        if (row.ActiveEnterAt is null || SeriesTime.TryParse(row.ActiveEnterAt, out var enter) == false)
        {
            return null;
        }

        if (before.ActiveEnterAt is null)
        {
            return enter;
        }

        return SeriesTime.TryParse(before.ActiveEnterAt, out var old) && enter > old ? enter : null;
    }

    /// <summary>
    /// Tells whether no boot lies between two service records: their boot IDs agree and no boot record is stored between them.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="row">The record</param>
    /// <param name="before">The predecessor</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <c>true</c> when no boot lies between</returns>
    private static async Task<bool> NoBootBetweenAsync(ITimeSeriesReader reader, ServiceStateRow row, ServiceStateRow before, CancellationToken cancellationToken)
    {
        if (row.BootId.Length > 0 && before.BootId.Length > 0 && row.BootId != before.BootId)
        {
            return false;
        }

        if (row.CapturedAt <= before.CapturedAt)
        {
            return true;
        }

        var boots = await reader.BootTimesAsync(new TimeRangeQuery
                                                {
                                                    From = before.CapturedAt,
                                                    To = row.CapturedAt,
                                                    Limit = 1
                                                },
                                                cancellationToken).ConfigureAwait(false);

        return boots.Count == 0;
    }

    /// <summary>
    /// Builds the marker of a restart, timed at the new enter time when that lies in the range and not after the record, else at the record's capture time.
    /// </summary>
    /// <param name="row">The record</param>
    /// <param name="before">The predecessor</param>
    /// <param name="range">The range</param>
    /// <returns>The marker</returns>
    private static SeriesMarker RestartMarker(ServiceStateRow row, ServiceStateRow before, SeriesRange range)
    {
        var at = row.CapturedAt;
        var enter = EnterMoved(row, before);

        if (enter is not null && enter >= range.From && enter < range.End && enter <= row.CapturedAt)
        {
            at = enter.Value;
        }

        return new SeriesMarker
               {
                   Kind = MarkerKind.ServiceRestart,
                   At = at,
                   Text = row.Unit,
                   BootId = row.BootId
               };
    }

    /// <summary>
    /// Adds the backup runs that overlap the range.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="items">The markers; the new ones are added</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the time up to which the backup runs are complete, or <c>null</c></returns>
    private static async Task<DateTimeOffset?> AddBackupsAsync(ITimeSeriesReader reader, SeriesRange range, List<(SeriesMarker Marker, long Id)> items, CancellationToken cancellationToken)
    {
        var read = await EventPages.ReadAsync(query => reader.BackupRunsAsync(query, cancellationToken),
                                              SeriesTime.Add(range.From, TimeSpan.FromDays(-1)),
                                              SeriesTime.Add(range.End, TimeSpan.FromDays(1)),
                                              row => row.CapturedAt,
                                              row => row.Id,
                                              cancellationToken).ConfigureAwait(false);
        var runs = read.Rows.Where(row => SeriesTime.TryParse(row.StartedAt, out _))
                            .GroupBy(row => (row.BootId, Start: ParsedStart(row)))
                            .Select(group => group.ToList())
                            .OrderBy(group => ParsedStart(group[0]))
                            .Take(TimeSeriesService.MaxMarkers + 1)
                            .ToList();

        foreach (var run in runs)
        {
            var marker = await BackupMarkerAsync(reader, range, run, cancellationToken).ConfigureAwait(false);

            if (marker is not null)
            {
                items.Add((marker, run[0].Id));
            }
        }

        return read.RanOutAt is null ? null : SeriesTime.Add(read.RanOutAt.Value, TimeSpan.FromDays(-1));
    }

    /// <summary>
    /// Parses the start of a backup run record, which must parse.
    /// </summary>
    /// <param name="row">The record</param>
    /// <returns>The start</returns>
    private static DateTimeOffset ParsedStart(BackupRunRow row)
    {
        return SeriesTime.TryParse(row.StartedAt, out var start) ? start : default;
    }

    /// <summary>
    /// Parses the time a backup run was found gone.
    /// </summary>
    /// <param name="row">The record</param>
    /// <returns>The time, or <c>null</c> when the record has none that parses</returns>
    private static DateTimeOffset? ParsedGone(BackupRunRow row)
    {
        return SeriesTime.TryParse(row.GoneBy, out var gone) ? gone : null;
    }

    /// <summary>
    /// Builds the marker of one backup run from its records, or <c>null</c> when it started at or after the range's end or ended before its start.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="records">The records of the run: its start records and its end record</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the marker, or <c>null</c></returns>
    private static async Task<SeriesMarker?> BackupMarkerAsync(ITimeSeriesReader reader, SeriesRange range, List<BackupRunRow> records, CancellationToken cancellationToken)
    {
        var start = ParsedStart(records[0]);

        if (start >= range.End)
        {
            return null;
        }

        var command = records.Select(row => row.Command).FirstOrDefault(text => text.Length > 0) ?? string.Empty;
        var end = records.FirstOrDefault(row => row.Phase == BackupRun.PhaseEnd && ParsedGone(row) is not null);

        if (end is null)
        {
            var boots = await reader.BootTimesAsync(new TimeRangeQuery
                                                    {
                                                        From = SeriesTime.Add(start, TimeSpan.FromTicks(1)),
                                                        To = SeriesTime.Add(range.Now, TimeSpan.FromTicks(1)),
                                                        Limit = 1
                                                    },
                                                    cancellationToken).ConfigureAwait(false);

            return Backup(records[0], start, command, null, null, boots.Count == 0 ? BackupRunEnd.Running : BackupRunEnd.Unknown);
        }

        var endsAt = Later(start, Earlier(ParsedGone(end) ?? start, range.Now));

        if (endsAt <= range.From)
        {
            return null;
        }

        var seen = SeriesTime.TryParse(end.LastSeenAt, out var lastSeen) ? Earlier(lastSeen, range.Now) : (DateTimeOffset?)null;

        return Backup(records[0], start, command, endsAt, seen, BackupRunEnd.Ended);
    }

    /// <summary>
    /// Builds the marker of a backup run.
    /// </summary>
    /// <param name="first">The first record of the run</param>
    /// <param name="start">The start</param>
    /// <param name="command">The command</param>
    /// <param name="endsAt">The end, or <c>null</c></param>
    /// <param name="seen">The last sighting, or <c>null</c></param>
    /// <param name="end">How the run ended</param>
    /// <returns>The marker</returns>
    private static SeriesMarker Backup(BackupRunRow first, DateTimeOffset start, string command, DateTimeOffset? endsAt, DateTimeOffset? seen, BackupRunEnd end)
    {
        return new SeriesMarker
               {
                   Kind = MarkerKind.BackupRun,
                   At = start,
                   EndsAt = endsAt,
                   LastSeenAt = seen,
                   End = end,
                   Text = command,
                   BootId = first.BootId
               };
    }

    /// <summary>
    /// Returns the earlier of two instants.
    /// </summary>
    /// <param name="first">The first instant</param>
    /// <param name="second">The second instant</param>
    /// <returns>The earlier instant</returns>
    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second)
    {
        return first < second ? first : second;
    }

    /// <summary>
    /// Returns the later of two instants.
    /// </summary>
    /// <param name="first">The first instant</param>
    /// <param name="second">The second instant</param>
    /// <returns>The later instant</returns>
    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second)
    {
        return first > second ? first : second;
    }

    #endregion // Methods
}