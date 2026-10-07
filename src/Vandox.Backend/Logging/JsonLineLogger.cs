using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Logging;

/// <summary>
/// A logger of <see cref="JsonLineLoggerProvider"/>.
/// </summary>
internal sealed partial class JsonLineLogger : ILogger
{
    #region Constants

    private const string OriginalFormat = "{OriginalFormat}";

    #endregion // Constants

    #region Fields

    private readonly JsonLineLoggerProvider _provider;
    private readonly string _category;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonLineLogger"/> class.
    /// </summary>
    /// <param name="provider">The provider that writes the lines</param>
    /// <param name="category">The category of the logger</param>
    internal JsonLineLogger(JsonLineLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Returns the name of a level as slog names it.
    /// </summary>
    /// <param name="level">The level</param>
    /// <returns>The name</returns>
    private static string LevelName(LogLevel level)
    {
        return level switch
               {
                   LogLevel.Trace or LogLevel.Debug => "DEBUG",
                   LogLevel.Information => "INFO",
                   LogLevel.Warning => "WARN",
                   _ => "ERROR"
               };
    }

    /// <summary>
    /// Returns the text of a log call without its placeholders: the fixed text of a message template, or the formatted message
    /// when the call has no template.
    /// </summary>
    /// <typeparam name="TState">The type of the state</typeparam>
    /// <param name="state">The state</param>
    /// <param name="exception">The exception</param>
    /// <param name="formatter">The formatter of the call</param>
    /// <returns>The text</returns>
    private static string Message<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> attributes && attributes.FirstOrDefault(attribute => attribute.Key == OriginalFormat).Value is string template)
        {
            return PlaceholderPattern().Replace(template, string.Empty).Trim();
        }

        return formatter(state, exception);
    }

    /// <summary>
    /// Converts a placeholder name such as <c>SourceType</c> to the attribute name <c>source_type</c>.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns>The attribute name</returns>
    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsUpper(name[index]) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Creates the pattern of a placeholder in a message template, with the space before it.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"\s*\{[A-Za-z0-9_@$]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Writes a value as a JSON value.
    /// </summary>
    /// <param name="writer">The writer</param>
    /// <param name="name">The name of the attribute</param>
    /// <param name="value">The value</param>
    private static void WriteAttribute(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;

            case bool flag:
                writer.WriteBoolean(name, flag);
                break;

            case int number:
                writer.WriteNumber(name, number);
                break;

            case long number:
                writer.WriteNumber(name, number);
                break;

            case double number:
                writer.WriteNumber(name, number);
                break;

            default:
                writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    #endregion // Methods

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
        return _provider.IsEnabled(logLevel);
    }

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (_provider.IsEnabled(logLevel))
        {
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("time", _provider.Now().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
                writer.WriteString("level", LevelName(logLevel));
                writer.WriteString("msg", Message(state, exception, formatter));

                if (state is IReadOnlyList<KeyValuePair<string, object?>> attributes)
                {
                    foreach (var (name, value) in attributes.Where(attribute => attribute.Key != OriginalFormat))
                    {
                        WriteAttribute(writer, SnakeCase(name), value);
                    }
                }

                if (exception is not null)
                {
                    writer.WriteString("exception", exception.GetType().Name);
                }

                writer.WriteString("logger", _category);
                writer.WriteEndObject();
            }

            _provider.Write(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    #endregion // ILogger
}