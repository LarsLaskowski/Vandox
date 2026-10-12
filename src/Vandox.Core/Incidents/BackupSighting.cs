namespace Vandox.Core.Incidents;

/// <summary>
/// A process snapshot that holds a backup program.
/// </summary>
internal sealed class BackupSighting
{
    #region Properties

    /// <summary>
    /// Gets the capture time of the snapshot.
    /// </summary>
    internal DateTimeOffset At { get; }

    /// <summary>
    /// Gets the record ID of the snapshot.
    /// </summary>
    internal long RecordId { get; }

    /// <summary>
    /// Gets the first backup program found.
    /// </summary>
    internal string Program { get; }

    /// <summary>
    /// Gets the summed resident memory of the backup programs.
    /// </summary>
    internal long RssBytes { get; }

    #endregion // Properties

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupSighting"/> class.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="recordId">The record ID</param>
    /// <param name="programs">The programs of the snapshot</param>
    /// <param name="found">The backup programs found, at least one</param>
    internal BackupSighting(DateTimeOffset at, long recordId, IReadOnlyDictionary<string, ProgramUsage> programs, IReadOnlyList<string> found)
    {
        var sum = 0UL;

        foreach (var name in found)
        {
            var rss = programs[name].RssBytes;

            sum = ulong.MaxValue - sum < rss ? ulong.MaxValue : sum + rss;
        }

        At = at;
        RecordId = recordId;
        Program = found[0];
        RssBytes = sum > long.MaxValue ? long.MaxValue : (long)sum;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Tells whether this sighting comes before another one.
    /// </summary>
    /// <param name="at">The capture time of the other</param>
    /// <param name="recordId">The record ID of the other</param>
    /// <returns><c>true</c> when this one is earlier</returns>
    internal bool IsBefore(DateTimeOffset at, long recordId)
    {
        return At < at || (At == at && RecordId < recordId);
    }

    /// <summary>
    /// Creates the timeline entry of this sighting.
    /// </summary>
    /// <param name="type">The entry type</param>
    /// <returns>The entry</returns>
    internal TimelineEntry ToEntry(string type)
    {
        return new TimelineEntry
               {
                   At = At,
                   Type = type,
                   Texts = new Dictionary<string, string>(StringComparer.Ordinal)
                           {
                               ["program"] = Program
                           },
                   Numbers = new Dictionary<string, long>(StringComparer.Ordinal)
                             {
                                 ["rss_bytes"] = RssBytes
                             },
                   RecordIds = [RecordId],
                   LineCount = 1
               };
    }

    #endregion // Methods
}