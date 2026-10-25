using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Tests;

/// <summary>
/// One log line of the ingest endpoint: its level, its fixed text and the values of its structured attributes.
/// </summary>
internal sealed class IngestLogEntry
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestLogEntry"/> class.
    /// </summary>
    /// <param name="level">The level</param>
    /// <param name="name">The fixed text of the line, without the attribute placeholders</param>
    /// <param name="values">The attribute values as text, keyed by the placeholder name; <c>null</c> for a value that was <c>null</c></param>
    /// <param name="text">The line as the formatter renders it</param>
    internal IngestLogEntry(LogLevel level, string name, Dictionary<string, string?> values, string text)
    {
        Level = level;
        Name = name;
        Values = values;
        Text = text;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the level.
    /// </summary>
    internal LogLevel Level { get; }

    /// <summary>
    /// Gets the fixed text of the line.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the attribute values as text.
    /// </summary>
    internal Dictionary<string, string?> Values { get; }

    /// <summary>
    /// Gets the line as the formatter renders it.
    /// </summary>
    internal string Text { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gets the value of an attribute.
    /// </summary>
    /// <param name="name">The placeholder name, such as <c>AgentId</c></param>
    /// <returns>The value as text, <c>null</c> when the attribute is absent or was <c>null</c></returns>
    internal string? Value(string name)
    {
        return Values.GetValueOrDefault(name);
    }

    #endregion // Methods
}