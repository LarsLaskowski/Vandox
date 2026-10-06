using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Logging;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="JsonLineLoggerProvider"/> and <see cref="LogLevels"/>
/// </summary>
[TestClass]
public class JsonLineLoggerTests
{
    #region Methods

    /// <summary>
    /// A message with attributes becomes one JSON object with the fixed text as message and the attributes in snake case.
    /// </summary>
    [TestMethod]
    public void JsonLineLoggerWritesObjectWithAttributes()
    {
        // Arrange
        var output = new StringWriter();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 30, 15, TimeSpan.Zero));

        using var factory = BackendLogging.Create(output, "info", clock)!;

        // Act
        factory.CreateLogger("test").FileFinished("a.log", "imported", "syslog", 10, 9, 1, "why");

        var line = JsonDocument.Parse(output.ToString().TrimEnd()).RootElement;

        // Assert
        Assert.AreEqual("INFO", line.GetProperty("level").GetString(), "level");
        Assert.AreEqual("file finished", line.GetProperty("msg").GetString(), "the fixed text without placeholders");
        Assert.AreEqual("a.log", line.GetProperty("path").GetString(), "string attribute");
        Assert.AreEqual("syslog", line.GetProperty("source_type").GetString(), "snake case attribute name");
        Assert.AreEqual(10L, line.GetProperty("lines").GetInt64(), "number attribute");
        Assert.StartsWith("2026-10-05T12:30:15", line.GetProperty("time").GetString()!, "time from the clock");
        Assert.AreEqual("test", line.GetProperty("logger").GetString(), "category");
    }

    /// <summary>
    /// Values from the input cannot inject lines or control characters into the log.
    /// </summary>
    [TestMethod]
    public void JsonLineLoggerEscapesHostileValues()
    {
        // Arrange
        var output = new StringWriter();

        using var factory = BackendLogging.Create(output, "info", TimeProvider.System)!;

        // Act
        factory.CreateLogger("test").ImportStarted("a\nfake line\u001b[31m\u0085​");

        // Assert
        Assert.AreEqual(1, output.ToString().TrimEnd().Split('\n').Length, "one line");
        Assert.DoesNotContain("\u001b", output.ToString(), "no escape character");
        Assert.DoesNotContain("\u0085", output.ToString(), "no C1 control");
        Assert.AreEqual("a\nfake line\u001b[31m\u0085​", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("path").GetString(), "the value round-trips");
    }

    /// <summary>
    /// The level decides what is written, and exceptions and every attribute type are written.
    /// </summary>
    [TestMethod]
    public void JsonLineLoggerFiltersLevelsAndWritesExceptionAndTypes()
    {
        // Arrange
        var output = new StringWriter();

        using var factory = BackendLogging.Create(output, "warn", TimeProvider.System)!;

        var logger = factory.CreateLogger("test");

        // Act
        logger.ImportStarted("filtered");
        logger.ImportFailed(new InvalidOperationException("secret"), "boom");
        logger.Log(LogLevel.Error,
                   new EventId(1),
                   new List<KeyValuePair<string, object?>>
                   {
                       new("Flag", true),
                       new("Ratio", 1.5),
                       new("Nothing", null),
                       new("Other", new Uri("http://x/")),
                       new("Big", 5L)
                   },
                   null,
                   (_, _) => "plain message");
        logger.Log(LogLevel.None, new EventId(0), "never", null, (text, _) => text);

        var lines = output.ToString().TrimEnd().Split('\n').Select(line => JsonDocument.Parse(line).RootElement).ToList();

        // Assert
        Assert.AreEqual(2, lines.Count, "the info line is filtered, the error lines are written");
        Assert.AreEqual("ERROR", lines[0].GetProperty("level").GetString(), "level");
        Assert.AreEqual("InvalidOperationException", lines[0].GetProperty("exception").GetString(), "the exception type is written, never its message");
        Assert.DoesNotContain("secret", output.ToString(), "the exception message stays out of the log");
        Assert.AreEqual("plain message", lines[1].GetProperty("msg").GetString(), "formatted message without template");
        Assert.IsTrue(lines[1].GetProperty("flag").GetBoolean(), "boolean attribute");
        Assert.AreEqual(1.5, lines[1].GetProperty("ratio").GetDouble(), "double attribute");
        Assert.AreEqual(JsonValueKind.Null, lines[1].GetProperty("nothing").ValueKind, "null attribute");
        Assert.AreEqual("http://x/", lines[1].GetProperty("other").GetString(), "other attribute");
        Assert.AreEqual(5L, lines[1].GetProperty("big").GetInt64(), "long attribute");
    }

    /// <summary>
    /// The configured level names map to log levels, and an unknown one is refused.
    /// </summary>
    [TestMethod]
    public void LogLevelsTryParseMapsNames()
    {
        // Act
        var debug = LogLevels.TryParse("debug", out var debugLevel);
        var info = LogLevels.TryParse("info", out var infoLevel);
        var warn = LogLevels.TryParse("warn", out var warnLevel);
        var error = LogLevels.TryParse("error", out var errorLevel);
        var unknown = LogLevels.TryParse("verbose", out _);

        // Assert
        Assert.IsTrue(debug && info && warn && error, "known names");
        Assert.AreEqual(LogLevel.Debug, debugLevel, "debug");
        Assert.AreEqual(LogLevel.Information, infoLevel, "info");
        Assert.AreEqual(LogLevel.Warning, warnLevel, "warn");
        Assert.AreEqual(LogLevel.Error, errorLevel, "error");
        Assert.IsFalse(unknown, "unknown name");
        Assert.IsNull(BackendLogging.Create(new StringWriter(), "verbose", TimeProvider.System), "no factory for an unknown level");
    }

    /// <summary>
    /// Debug and trace calls are named DEBUG, and scopes are not supported.
    /// </summary>
    [TestMethod]
    public void JsonLineLoggerNamesDebugAndIgnoresScopes()
    {
        // Arrange
        var output = new StringWriter();

        using var provider = new JsonLineLoggerProvider(output, LogLevel.Trace, TimeProvider.System);

        var logger = provider.CreateLogger("test");

        // Act
        var scope = logger.BeginScope("scope");

        logger.Log(LogLevel.Trace, new EventId(0), "trace", null, (text, _) => text);
        logger.Log(LogLevel.Debug, new EventId(0), "debug", null, (text, _) => text);

        // Assert
        Assert.IsNull(scope, "no scope");
        Assert.AreEqual(2, output.ToString().Split("\"level\":\"DEBUG\"").Length - 1, "both are DEBUG");
    }

    #endregion // Methods
}