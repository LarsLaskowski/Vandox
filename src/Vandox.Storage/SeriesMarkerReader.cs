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
        var restarts = await ReadRestartsAsync(reader, range, items, cancellationToken).ConfigureAwait(false);
        var backups = await ReadBackupsAsync(reader, range, items, cancellationToken).ConfigureAwait(false);
        var intervals = restarts.Checks.Select(check => (check.Before.CapturedAt, check.Row.CapturedAt))
                                .Concat(backups.Checks.Select(check => (SeriesTime.Add(check.Start, TimeSpan.FromTicks(1)), SeriesTime.Add(range.Now, TimeSpan.FromTicks(1)))))
                                .ToList();
        var boots = await SeriesBootIndex.ReadAsync(reader, intervals, cancellationToken).ConfigureAwait(false);

        bound = SeriesTime.Earlier(bound, restarts.Bound);
        bound = SeriesTime.Earlier(bound, backups.Bound);
        bound = SeriesTime.Earlier(bound, DecideRestarts(restarts.Checks, boots, range, items));
        bound = SeriesTime.Earlier(bound, DecideBackups(backups.Checks, boots, range, items));

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
    /// Adds the service restarts that need no boot read and lists the checks that do.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="items">The markers; the new ones are added</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the time up to which the restarts are complete, or <c>null</c>, and the records whose restart depends on the boots</returns>
    private static async Task<(DateTimeOffset? Bound, List<(ServiceStateRow Row, ServiceStateRow Before)> Checks)> ReadRestartsAsync(ITimeSeriesReader reader, SeriesRange range, List<(SeriesMarker Marker, long Id)> items, CancellationToken cancellationToken)
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
        var checks = new List<(ServiceStateRow Row, ServiceStateRow Before)>();

        var previous = await PreviousAsync(reader, rows, range.From, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var before = row.Previous ?? previous.GetValueOrDefault(row.Unit);

            if (before is null)
            {
                continue;
            }

            if (row.Restarts > before.Restarts)
            {
                items.Add((RestartMarker(row, before, range), 0));
            }
            else if (EnterMoved(row, before) is not null && _runningStates.Contains(before.ActiveState) && BootIdsAgree(row, before))
            {
                if (row.CapturedAt <= before.CapturedAt)
                {
                    items.Add((RestartMarker(row, before, range), 0));
                }
                else
                {
                    checks.Add((row, before));
                }
            }
        }

        return (bound, checks);
    }

    /// <summary>
    /// Adds the restarts whose boot check the boot read decides.
    /// </summary>
    /// <param name="checks">The records whose restart depends on the boots</param>
    /// <param name="boots">The boots read</param>
    /// <param name="range">The range</param>
    /// <param name="items">The markers; the new ones are added</param>
    /// <returns>The time its markers would have of the earliest undecided check, never before the range, or <c>null</c> when every check is decided</returns>
    private static DateTimeOffset? DecideRestarts(List<(ServiceStateRow Row, ServiceStateRow Before)> checks, SeriesBootIndex boots, SeriesRange range, List<(SeriesMarker Marker, long Id)> items)
    {
        DateTimeOffset? bound = null;

        foreach (var (row, before) in checks)
        {
            var between = boots.Contains(before.CapturedAt, row.CapturedAt);

            if (between is null)
            {
                bound = SeriesTime.Earlier(bound, RestartMarker(row, before, range).At);
            }
            else if (between == false)
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
    /// Tells whether the boot IDs of two service records leave room for a restart: they agree or one is missing.
    /// </summary>
    /// <param name="row">The record</param>
    /// <param name="before">The predecessor</param>
    /// <returns><c>false</c> when the records carry different boot IDs, which is a boot between them</returns>
    private static bool BootIdsAgree(ServiceStateRow row, ServiceStateRow before)
    {
        return row.BootId.Length == 0 || before.BootId.Length == 0 || row.BootId == before.BootId;
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
    /// Adds the backup runs that overlap the range and have an end, and lists the runs without one.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="items">The markers; the new ones are added</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the time up to which the backup runs are complete, or <c>null</c>, and the runs whose state depends on the boots</returns>
    private static async Task<(DateTimeOffset? Bound, List<(List<BackupRunRow> Records, DateTimeOffset Start)> Checks)> ReadBackupsAsync(ITimeSeriesReader reader, SeriesRange range, List<(SeriesMarker Marker, long Id)> items, CancellationToken cancellationToken)
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
        var checks = new List<(List<BackupRunRow> Records, DateTimeOffset Start)>();

        foreach (var run in runs)
        {
            var start = ParsedStart(run[0]);

            if (start >= range.End)
            {
                continue;
            }

            var marker = BackupMarker(range, run, start);

            if (marker is not null)
            {
                items.Add((marker, run[0].Id));
            }
            else if (run.Any(row => row.Phase == BackupRun.PhaseEnd && ParsedGone(row) is not null) == false)
            {
                checks.Add((run, start));
            }
        }

        return (read.RanOutAt is null ? null : SeriesTime.Add(read.RanOutAt.Value, TimeSpan.FromDays(-1)), checks);
    }

    /// <summary>
    /// Adds the backup runs without an end whose state the boot read decides: unknown when a boot follows the start, else running.
    /// </summary>
    /// <param name="checks">The runs without an end</param>
    /// <param name="boots">The boots read</param>
    /// <param name="range">The range</param>
    /// <param name="items">The markers; the new ones are added</param>
    /// <returns>The time of the earliest undecided run, never before the range, or <c>null</c> when every run is decided</returns>
    private static DateTimeOffset? DecideBackups(List<(List<BackupRunRow> Records, DateTimeOffset Start)> checks, SeriesBootIndex boots, SeriesRange range, List<(SeriesMarker Marker, long Id)> items)
    {
        DateTimeOffset? bound = null;

        foreach (var (records, start) in checks)
        {
            var followed = boots.Contains(SeriesTime.Add(start, TimeSpan.FromTicks(1)), SeriesTime.Add(range.Now, TimeSpan.FromTicks(1)));

            if (followed is null)
            {
                bound = SeriesTime.Earlier(bound, Later(start, range.From));

                continue;
            }

            var command = records.Select(row => row.Command).FirstOrDefault(text => text.Length > 0) ?? string.Empty;

            items.Add((Backup(records[0], start, command, null, null, followed == true ? BackupRunEnd.Unknown : BackupRunEnd.Running), records[0].Id));
        }

        return bound;
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
    /// Builds the marker of one backup run that has an end from its records, or <c>null</c> when it has none or ended before the range.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="records">The records of the run: its start records and its end record</param>
    /// <param name="start">The start of the run</param>
    /// <returns>The marker, or <c>null</c></returns>
    private static SeriesMarker? BackupMarker(SeriesRange range, List<BackupRunRow> records, DateTimeOffset start)
    {
        var end = records.FirstOrDefault(row => row.Phase == BackupRun.PhaseEnd && ParsedGone(row) is not null);

        if (end is null)
        {
            return null;
        }

        var command = records.Select(row => row.Command).FirstOrDefault(text => text.Length > 0) ?? string.Empty;
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