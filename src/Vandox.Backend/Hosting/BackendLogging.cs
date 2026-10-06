using Microsoft.Extensions.Logging;

using Vandox.Backend.Logging;

namespace Vandox.Backend;

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
            return LoggerFactory.Create(builder => builder.SetMinimumLevel(minimum).AddProvider(new JsonLineLoggerProvider(writer, minimum, clock)));
        }

        return null;
    }

    #endregion // Methods
}