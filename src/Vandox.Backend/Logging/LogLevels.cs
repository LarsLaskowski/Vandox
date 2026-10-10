using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Logging;

/// <summary>
/// Maps the configured log level names to log levels.
/// </summary>
public static class LogLevels
{
    #region Methods

    /// <summary>
    /// Maps <c>debug</c>, <c>info</c>, <c>warn</c> and <c>error</c> to a log level.
    /// </summary>
    /// <param name="name">The configured name</param>
    /// <param name="level">The level</param>
    /// <returns><c>true</c> when the name is known</returns>
    public static bool TryParse(string name, out LogLevel level)
    {
        switch (name)
        {
            case "debug":
                {
                    level = LogLevel.Debug;

                    return true;
                }
            case "info":
                {
                    level = LogLevel.Information;

                    return true;
                }
            case "warn":
                {
                    level = LogLevel.Warning;

                    return true;
                }
            case "error":
                {
                    level = LogLevel.Error;

                    return true;
                }
            default:
                {
                    level = LogLevel.None;

                    return false;
                }
        }
    }

    #endregion // Methods
}