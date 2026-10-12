using Vandox.Backend.Hosting;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

#pragma warning disable RH2003, S2325
/// <summary>
/// <c>vandoxd templates</c>: rebuilds the log template statistics from the stored log lines. The summary goes to standard
/// output, progress to standard error as JSON lines.
/// </summary>
internal static class TemplatesCommand
{
    #region Methods

    /// <summary>
    /// Runs the sub-command.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="stdout">Standard output: the summary</param>
    /// <param name="stderr">Standard error: the log</param>
    /// <param name="hooks">The hooks of the host</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code: 0 when the statistics are rebuilt, 1 otherwise</returns>
    internal static Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Formats the summary of a rebuild.
    /// </summary>
    /// <param name="result">The result</param>
    /// <returns>The three summary lines, each ended by a line feed</returns>
    internal static string FormatSummary(TemplateRebuildResult result)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}