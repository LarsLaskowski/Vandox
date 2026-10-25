using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Tests;

/// <summary>
/// A logger and logger provider that keeps every line with its structured attributes, so a test reads the values a log method got.
/// </summary>
internal sealed class IngestLogCapture : ILoggerProvider, ILogger
{
    #region Constants

    private const string OriginalFormat = "{OriginalFormat}";

    #endregion // Constants

    #region Fields

    private readonly Lock _lock = new();

    private readonly List<IngestLogEntry> _entries = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets a copy of the lines logged so far, in order.
    /// </summary>
    internal IReadOnlyList<IngestLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates a logger factory whose loggers all write into this capture, at every level.
    /// </summary>
    /// <returns>The factory</returns>
    internal ILoggerFactory CreateFactory()
    {
        return LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(this));
    }

    /// <summary>
    /// Gets the lines with a fixed text.
    /// </summary>
    /// <param name="name">The fixed text</param>
    /// <returns>The lines in order</returns>
    internal List<IngestLogEntry> Named(string name)
    {
        return Entries.Where(entry => entry.Name == name).ToList();
    }

    /// <summary>
    /// Gets every text a line carries: its name, its rendering and its attribute values.
    /// </summary>
    /// <returns>The texts of all lines</returns>
    internal List<string> AllTexts()
    {
        var texts = new List<string>();

        foreach (var entry in Entries)
        {
            texts.Add(entry.Name);
            texts.Add(entry.Text);
            texts.AddRange(entry.Values.Values.Where(value => value is not null).Select(value => value!));
        }

        return texts;
    }

    #endregion // Methods

    #region ILoggerProvider

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return this;
    }

    #endregion // ILoggerProvider

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
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var name = string.Empty;

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (pair.Key == OriginalFormat)
                {
                    var template = pair.Value?.ToString() ?? string.Empty;
                    var cut = template.IndexOf(" {", StringComparison.Ordinal);

                    name = cut < 0 ? template : template[..cut];
                }
                else
                {
                    values[pair.Key] = pair.Value?.ToString();
                }
            }
        }

        lock (_lock)
        {
            _entries.Add(new IngestLogEntry(logLevel, name, values, formatter(state, exception)));
        }
    }

    #endregion // ILogger

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        // The capture holds nothing to release.
    }

    #endregion // IDisposable
}