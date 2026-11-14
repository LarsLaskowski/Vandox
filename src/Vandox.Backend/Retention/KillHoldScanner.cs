using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Retention;

/// <summary>
/// Scans the stored kernel events for OOM kills and keeps the window of each kill as a hold.
/// </summary>
internal sealed class KillHoldScanner
{
    #region Constants

    /// <summary>
    /// The number of kernel events read and saved in one page.
    /// </summary>
    internal const int PageSize = 1000;

    /// <summary>
    /// The margin added to both ends of a kill window, so that the lines the incident reads around the kill stay inside the hold.
    /// </summary>
    internal static readonly TimeSpan WindowMargin = TimeSpan.FromMinutes(5);

    #endregion // Constants

    #region Fields

    private readonly IRetentionStore _store;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="KillHoldScanner"/> class.
    /// </summary>
    /// <param name="store">The retention store</param>
    internal KillHoldScanner(IRetentionStore store)
    {
        _store = store;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Scans the kernel events not scanned yet and saves the holds of their OOM kills.
    /// </summary>
    /// <param name="policy">The retention policy that holds the kill window</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that returns the number of holds added</returns>
    internal async Task<int> ScanAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var progress = await _store.KillScanProgressAsync(cancellationToken).ConfigureAwait(false);
        var scanned = progress.ScannedId;
        var added = 0;

        while (scanned < progress.HighestId)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _store.ScanRecordsAsync(new KillScanQuery
                                                     {
                                                         AboveId = scanned,
                                                         Limit = PageSize
                                                     },
                                                     cancellationToken)
                                   .ConfigureAwait(false);
            var inRange = page.Where(record => record.Id <= progress.HighestId).ToList();
            var last = page.Count == PageSize && inRange.Count == PageSize ? inRange[^1].Id : progress.HighestId;
            var holds = inRange.Where(IsAgentKill).Select(record => CreateHold(record, policy)).Where(hold => IsKept(hold, policy, now)).ToList();

            await _store.SaveKillScanAsync(holds,
                                           new KillScanProgress
                                           {
                                               ScannedId = last,
                                               HighestId = progress.HighestId
                                           },
                                           cancellationToken)
                        .ConfigureAwait(false);

            added += holds.Count;
            scanned = last;
        }

        return added;
    }

    /// <summary>
    /// Determines whether a record is an OOM kill that the agent's kernel collector reported.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns><c>true</c> for a kernel event of type OOM kill with origin agent</returns>
    private static bool IsAgentKill(StoredRecord record)
    {
        return record.Record.Origin == RecordOrigin.Agent && record.Record.Data is KernelEvent { Type: KernelEvent.TypeOomKill };
    }

    /// <summary>
    /// Determines whether a hold still keeps data: not when it ended before the period of the incidents.
    /// </summary>
    /// <param name="hold">The hold</param>
    /// <param name="policy">The policy with the incident period</param>
    /// <param name="now">The current time</param>
    /// <returns><c>true</c> when the hold is saved</returns>
    private static bool IsKept(RetentionHold hold, RetentionPolicy policy, DateTimeOffset now)
    {
        return policy.Incidents is not { } period || hold.End >= now - period;
    }

    /// <summary>
    /// Creates the hold of a kill: its window before and after the capture time, each end widened by the margin.
    /// </summary>
    /// <param name="record">The kernel event of the kill</param>
    /// <param name="policy">The policy with the kill window</param>
    /// <returns>The hold</returns>
    private static RetentionHold CreateHold(StoredRecord record, RetentionPolicy policy)
    {
        var at = record.Record.CapturedAt;

        var start = at - policy.KillWindowBefore - WindowMargin;
        var end = at + policy.KillWindowAfter + WindowMargin;

        return new RetentionHold
               {
                   Start = start < StorableTime.Min ? StorableTime.Min : start,
                   End = end > StorableTime.Max ? StorableTime.Max : end
               };
    }

    #endregion // Methods
}