using System.Diagnostics.CodeAnalysis;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures the rollups of connection snapshots (issue #486, <c>docs/BENCHMARKS.md</c>): the fold steps beside stored metrics and process snapshots, a worst-case
/// step, the disk growth of a day, the reads over one year and 1,095 days, the trim of an untrimmed hour and the duration of schema step 17.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class ConnectionBenchmark
{
    #region Constants

    /// <summary>
    /// The option that selects this measurement.
    /// </summary>
    internal const string Option = "--connections";

    #endregion // Constants

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <summary>
    /// Runs the measurement in databases in a new folder below a directory and prints the figures.
    /// </summary>
    /// <param name="directory">The existing directory on the volume to measure</param>
    /// <param name="cancellationToken">Interrupts the measurement</param>
    /// <returns>A task that completes when the measurement is done</returns>
    internal static Task RunAsync(string directory, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    // Skeleton: removed with the implementation
#pragma warning restore RH2003

    #endregion // Methods
}