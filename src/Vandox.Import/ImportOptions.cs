using Vandox.Core.LogParsing;

namespace Vandox.Import;

/// <summary>
/// Configures <see cref="Importer.RunAsync"/>.
/// </summary>
public sealed class ImportOptions
{
    #region Properties

    /// <summary>
    /// Gets or sets the registry that picks a parser per file; required.
    /// </summary>
    public ParserRegistry? Parsers { get; set; }

    /// <summary>
    /// Gets or sets the store; required.
    /// </summary>
    public IImportStore? Store { get; set; }

    /// <summary>
    /// Gets or sets the clock; required. Times are converted to UTC.
    /// </summary>
    public TimeProvider? Clock { get; set; }

    /// <summary>
    /// Gets or sets the progress callback; optional, called synchronously.
    /// </summary>
    public Action<ImportProgress>? Progress { get; set; }

    /// <summary>
    /// Gets or sets the number of records per batch; 0 selects the default, at most <see cref="Storage.StorageLimits.MaxBatchRecords"/>.
    /// </summary>
    public int BatchRecords { get; set; }

    /// <summary>
    /// Gets or sets the number of input bytes per batch; 0 selects the default.
    /// </summary>
    public long BatchBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of decompressed bytes between two scan progress events; 0 selects the default.
    /// </summary>
    public long ProgressBytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether paths are resolved without <c>openat2</c>, as on a kernel that lacks it.
    /// Tests use it to run the fallback on a kernel that has it.
    /// </summary>
    internal bool ResolveWithoutKernel { get; set; }

    #endregion // Properties
}