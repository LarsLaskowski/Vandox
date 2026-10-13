using Vandox.Backend.Hosting;

namespace Vandox.Backend.Cli;

#pragma warning disable RH2003, S2325

/// <summary>
/// <c>vandoxd report</c>: writes the forensics report of the stored data as Markdown to standard output; the log goes to
/// standard error as JSON lines.
/// </summary>
internal static class ReportCommand
{
    #region Methods

    /// <summary>
    /// Runs the sub-command.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="language">The language of the report: <c>en</c> or <c>de</c></param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="stdout">Standard output: the report</param>
    /// <param name="stderr">Standard error: the log</param>
    /// <param name="hooks">The hooks of the host</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code: 0 when the report is written, 1 otherwise</returns>
    internal static Task<int> RunAsync(string configPath, string language, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}