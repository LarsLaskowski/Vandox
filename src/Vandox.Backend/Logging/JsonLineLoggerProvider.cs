using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Logging;

/// <summary>
/// Writes one JSON object per log call to a writer: <c>time</c>, <c>level</c>, <c>msg</c> and the structured attributes.
/// JSON escapes control characters and non-ASCII text in values, so a log line never injects extra lines, and it is
/// machine-readable in the container log.
/// </summary>
public sealed class JsonLineLoggerProvider : ILoggerProvider
{
    #region Fields

    private readonly TextWriter _writer;
    private readonly LogLevel _minimum;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonLineLoggerProvider"/> class.
    /// </summary>
    /// <param name="writer">Receives the log lines</param>
    /// <param name="minimum">The lowest level that is written</param>
    /// <param name="clock">The clock for the time of a line</param>
    public JsonLineLoggerProvider(TextWriter writer, LogLevel minimum, TimeProvider clock)
    {
        _writer = writer;
        _minimum = minimum;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Writes one log line.
    /// </summary>
    /// <param name="line">The finished JSON line without a line ending</param>
    internal void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    /// <summary>
    /// Returns the current time.
    /// </summary>
    /// <returns>The time in UTC</returns>
    internal DateTimeOffset Now()
    {
        return _clock.GetUtcNow();
    }

    /// <summary>
    /// Tells whether a level is written.
    /// </summary>
    /// <param name="level">The level</param>
    /// <returns><c>true</c> when the level is at or above the minimum</returns>
    internal bool IsEnabled(LogLevel level)
    {
        return level >= _minimum && level != LogLevel.None;
    }

    #endregion // Methods

    #region ILoggerProvider

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return new JsonLineLogger(this, categoryName);
    }

    #endregion // ILoggerProvider

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
    }

    #endregion // IDisposable
}