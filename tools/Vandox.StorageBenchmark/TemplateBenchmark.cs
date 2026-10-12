using System.Diagnostics.CodeAnalysis;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Measures writing log lines with their templates and the template queries (issue #22, <c>docs/BENCHMARKS.md</c>).
/// </summary>
#pragma warning disable RH2003, S2325, S1172
[ExcludeFromCodeCoverage]
internal static class TemplateBenchmark
{
    #region Methods

    /// <summary>
    /// Runs the measurement.
    /// </summary>
    /// <param name="directory">The directory for the temporary databases</param>
    /// <param name="lines">The number of log lines</param>
    /// <param name="size">The number of lines per batch</param>
    /// <param name="cancellationToken">Cancels the measurement</param>
    /// <returns>A task that completes when the measurement is printed</returns>
    internal static Task RunAsync(string directory, int lines, int size, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}