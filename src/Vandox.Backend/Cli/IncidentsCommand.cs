using Vandox.Backend.Hosting;

namespace Vandox.Backend.Cli;

#pragma warning disable RH2003, S2325

/// <summary>
/// <c>vandoxd incidents</c>: rebuilds the incidents from the stored data. The incidents and the summary go to standard
/// output, progress to standard error as JSON lines.
/// </summary>
internal static class IncidentsCommand
{
    #region Methods

    /// <summary>
    /// Loads the configuration, opens the database and rebuilds the incidents.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="environment">The environment</param>
    /// <param name="stdout">Receives the incidents and the summary</param>
    /// <param name="stderr">Receives the log lines</param>
    /// <param name="hooks">Provides the clock</param>
    /// <param name="cancellationToken">Interrupts the run</param>
    /// <returns>A task that returns the exit code: 0 when the run completed, 1 otherwise</returns>
    internal static Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}