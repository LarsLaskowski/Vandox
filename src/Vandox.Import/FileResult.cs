namespace Vandox.Import;

/// <summary>
/// The result of one file.
/// </summary>
public sealed class FileResult
{
    #region Properties

    /// <summary>
    /// Gets or sets the display path: relative to the root; archive entries as <c>archive path:entry name</c>.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source type; empty when the file was not recognized.
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what happened to the file.
    /// </summary>
    public ImportOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets why the file was not recognized or failed.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of lines read.
    /// </summary>
    public long Lines { get; set; }

    /// <summary>
    /// Gets or sets the number of records stored in this run.
    /// </summary>
    public long Records { get; set; }

    /// <summary>
    /// Gets or sets the number of lines the parser skipped.
    /// </summary>
    public long Skipped { get; set; }

    /// <summary>
    /// Gets or sets the number of records an earlier, interrupted run had stored.
    /// </summary>
    public long ResumedAfter { get; set; }

    /// <summary>
    /// Gets or sets the display name of the first import of the continued content; empty when the file is not continued.
    /// </summary>
    public string ContinuedFrom { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the first import of the content this file is the start of; empty when it is not contained.
    /// </summary>
    public string ContainedIn { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of lines of the content before the continuation point.
    /// </summary>
    public long ContinuedAfter { get; set; }

    /// <summary>
    /// Gets or sets the number of lines the parser skipped before its first record in a continued file; 0 otherwise.
    /// </summary>
    public long SkippedAtStart { get; set; }

    /// <summary>
    /// Gets or sets the earliest capture time of the records stored; <c>null</c> when none.
    /// </summary>
    public DateTimeOffset? First { get; set; }

    /// <summary>
    /// Gets or sets the latest capture time of the records stored; <c>null</c> when none.
    /// </summary>
    public DateTimeOffset? Last { get; set; }

    /// <summary>
    /// Gets the problems, at most <see cref="ImportLimits.MaxProblems"/>.
    /// </summary>
    public List<ImportProblem> Problems { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns a copy that is not changed any more.
    /// </summary>
    /// <returns>The copy</returns>
    public FileResult Snapshot()
    {
        var copy = new FileResult
                   {
                       Path = Path,
                       SourceType = SourceType,
                       Outcome = Outcome,
                       Reason = Reason,
                       Lines = Lines,
                       Records = Records,
                       Skipped = Skipped,
                       ResumedAfter = ResumedAfter,
                       ContinuedFrom = ContinuedFrom,
                       ContainedIn = ContainedIn,
                       ContinuedAfter = ContinuedAfter,
                       SkippedAtStart = SkippedAtStart,
                       First = First,
                       Last = Last
                   };

        copy.Problems.AddRange(Problems);

        return copy;
    }

    #endregion // Methods
}