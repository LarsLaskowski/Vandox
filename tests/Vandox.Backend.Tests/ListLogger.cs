using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Tests;

/// <summary>
/// A logger that keeps the formatted messages.
/// </summary>
internal sealed class ListLogger : ILogger
{
    #region Properties

    /// <summary>
    /// Gets the messages, formatted.
    /// </summary>
    internal List<string> Messages { get; } = [];

    /// <summary>
    /// Gets the level and event ID of each message, in the order of <see cref="Messages"/>.
    /// </summary>
    internal List<(LogLevel Level, EventId EventId)> Entries { get; } = [];

    /// <summary>
    /// Gets the exception of each message, or <c>null</c>, in the order of <see cref="Messages"/>.
    /// </summary>
    internal List<Exception?> Exceptions { get; } = [];

    #endregion // Properties

    #region ILogger

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        Entries.Add((logLevel, eventId));
        Exceptions.Add(exception);
    }

    #endregion // ILogger
}