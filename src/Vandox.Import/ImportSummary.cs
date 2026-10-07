namespace Vandox.Import;

/// <summary>
/// The result of a run.
/// </summary>
public sealed class ImportSummary
{
    #region Properties

    /// <summary>
    /// Gets or sets the root that was imported.
    /// </summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the run, in UTC.
    /// </summary>
    public DateTimeOffset Started { get; set; }

    /// <summary>
    /// Gets or sets the end of the run, in UTC.
    /// </summary>
    public DateTimeOffset Finished { get; set; }

    /// <summary>
    /// Gets the files found, in input order.
    /// </summary>
    public List<FileResult> Files { get; } = [];

    /// <summary>
    /// Gets or sets the number of lines read.
    /// </summary>
    public long Lines { get; set; }

    /// <summary>
    /// Gets or sets the number of records stored.
    /// </summary>
    public long Records { get; set; }

    /// <summary>
    /// Gets or sets the number of lines skipped.
    /// </summary>
    public long Skipped { get; set; }

    /// <summary>
    /// Gets or sets the earliest capture time of the records stored; <c>null</c> when none.
    /// </summary>
    public DateTimeOffset? First { get; set; }

    /// <summary>
    /// Gets or sets the latest capture time of the records stored; <c>null</c> when none.
    /// </summary>
    public DateTimeOffset? Last { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the run was interrupted by a cancellation.
    /// </summary>
    public bool Interrupted { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the number of files with an outcome.
    /// </summary>
    /// <param name="outcome">The outcome</param>
    /// <returns>The number of files</returns>
    public int Count(ImportOutcome outcome)
    {
        return Files.Count(file => file.Outcome == outcome);
    }

    #endregion // Methods
}