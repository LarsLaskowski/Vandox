using Microsoft.Extensions.Logging;

using Vandox.Backend.Logging;

namespace Vandox.Backend.Hosting;

/// <summary>
/// Creates the loggers of vandoxd: JSON lines on standard error.
/// </summary>
internal static class BackendLogging
{
    #region Methods

    /// <summary>
    /// Creates a logger factory that writes JSON lines to a writer.
    /// </summary>
    /// <param name="writer">Receives the log lines</param>
    /// <param name="level">The configured level: debug, info, warn or error</param>
    /// <param name="clock">The clock for the time of a line</param>
    /// <returns>The factory; <c>null</c> when the level is unknown</returns>
    internal static ILoggerFactory? Create(TextWriter writer, string level, TimeProvider clock)
    {
        if (LogLevels.TryParse(level, out var minimum))
        {
            // The framework logs every request at information level, the health probe every 30 s included; its
            // categories only speak from warning on unless the operator asks for debug.
            var framework = minimum <= LogLevel.Debug ? minimum : LogLevel.Warning;

            return LoggerFactory.Create(builder => builder.SetMinimumLevel(minimum).AddFilter("Microsoft", framework).AddProvider(new JsonLineLoggerProvider(writer, minimum, clock)));
        }

        return null;
    }

    #endregion // Methods
}