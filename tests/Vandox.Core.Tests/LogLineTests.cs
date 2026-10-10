using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LogLine"/>
/// </summary>
[TestClass]
public class LogLineTests
{
    #region Methods

    /// <summary>
    /// An empty event, a name and a name of 128 bytes are valid.
    /// </summary>
    /// <param name="value">The event</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("mariadb.start")]
    [DataRow("mariadb.shutdown_complete")]
    [DataRow("auth.failure")]
    public void LogLineValidateAcceptsAnEventThatIsEmptyOrAName(string value)
    {
        // Arrange
        var line = new LogLine
                   {
                       Log = "mysql/error.log",
                       Message = "x",
                       Event = value
                   };

        // Act
        var error = line.Validate();

        // Assert
        Assert.IsNull(error, "valid event");
    }

    /// <summary>
    /// A name of 128 bytes is valid and one of 129 bytes is too long.
    /// </summary>
    [TestMethod]
    public void LogLineValidateLimitsTheEventTo128Bytes()
    {
        // Arrange
        var longest = new LogLine
                      {
                          Log = "x",
                          Event = new string('a', 128)
                      };
        var tooLong = new LogLine
                      {
                          Log = "x",
                          Event = new string('a', 129)
                      };

        // Act
        var accepted = longest.Validate();
        var refused = tooLong.Validate();

        // Assert
        Assert.IsNull(accepted, "128 bytes");
        Assert.AreEqual("event", refused?.Field, "field of 129 bytes");
        Assert.AreEqual("too long", refused?.Reason, "reason of 129 bytes");
    }

    /// <summary>
    /// An event that is no name is refused with the field <c>event</c> and the reason <c>invalid characters</c>.
    /// </summary>
    /// <param name="value">The event</param>
    [TestMethod]
    [DataRow("mariadb start")]
    [DataRow("-x")]
    [DataRow("é")]
    [DataRow("a\n")]
    [DataRow("a,b")]
    public void LogLineValidateRefusesAnEventThatIsNoName(string value)
    {
        // Arrange
        var line = new LogLine
                   {
                       Log = "x",
                       Event = value
                   };

        // Act
        var error = line.Validate();

        // Assert
        Assert.AreEqual("event", error?.Field, "field");
        Assert.AreEqual("invalid characters", error?.Reason, "reason");
    }

    /// <summary>
    /// Through a record the field of a refused event is <c>data.event</c>.
    /// </summary>
    [TestMethod]
    public void LogLineValidateOfARecordNamesTheFieldDataEvent()
    {
        // Arrange
        var record = new DataRecord
                     {
                         Origin = RecordOrigin.Import,
                         Source = "mariadb",
                         CapturedAt = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
                         Data = new LogLine
                                {
                                    Log = "x",
                                    Event = "bad event"
                                }
                     };

        // Act
        var error = record.Validate();

        // Assert
        Assert.AreEqual("data.event", error?.Field, "field");
        Assert.AreEqual("invalid characters", error?.Reason, "reason");
    }

    /// <summary>
    /// The event is checked after the program and before the pid, the priority and the message.
    /// </summary>
    [TestMethod]
    public void LogLineValidateChecksTheEventAfterTheProgram()
    {
        // Arrange
        var programFirst = new LogLine
                           {
                               Log = "x",
                               Program = new string('p', 300),
                               Event = "bad event",
                               Pid = -1
                           };
        var eventFirst = new LogLine
                         {
                             Log = "x",
                             Event = "bad event",
                             Pid = -1,
                             Priority = 8,
                             Message = new string('m', ModelLimits.MaxTextBytes + 1)
                         };

        // Act
        var programError = programFirst.Validate();
        var eventError = eventFirst.Validate();

        // Assert
        Assert.AreEqual("program", programError?.Field, "the program comes first");
        Assert.AreEqual("event", eventError?.Field, "the event comes before pid, priority and message");
    }

    /// <summary>
    /// The event is written when set and as an empty string when empty, like host and program.
    /// </summary>
    [TestMethod]
    public void LogLineSerializeWritesTheEvent()
    {
        // Arrange
        var withEvent = new LogLine
                        {
                            Log = "mysql/error.log",
                            Message = "x",
                            Event = "mariadb.start"
                        };
        var without = new LogLine
                      {
                          Log = "mysql/error.log",
                          Message = "x"
                      };

        // Act
        var json = PayloadRegistry.Serialize(withEvent);
        var emptyJson = PayloadRegistry.Serialize(without);

        // Assert
        Assert.Contains("\"event\":\"mariadb.start\"", json, "the event when set");
        Assert.Contains("\"event\":\"\"", emptyJson, "an empty event as an empty string");
        Assert.DoesNotContain("\"event\":null", emptyJson, "never null");
    }

    /// <summary>
    /// A log line without an event, and one with an empty event, read back with an empty event that validates.
    /// </summary>
    /// <param name="json">The payload</param>
    [TestMethod]
    [DataRow("""{"log":"journal","message":"hello"}""")]
    [DataRow("""{"log":"journal","message":"hello","event":""}""")]
    public void LogLineDeserializeReadsAMissingOrEmptyEventAsEmpty(string json)
    {
        // Act
        var line = (LogLine)PayloadRegistry.Deserialize(RecordKind.LogLine, JsonDocument.Parse(json).RootElement)!;

        // Assert
        Assert.AreEqual(string.Empty, line.Event, "event");
        Assert.IsNull(line.Validate(), "the line is valid");
    }

    /// <summary>
    /// The event survives a round trip.
    /// </summary>
    [TestMethod]
    public void LogLineEventSurvivesARoundTrip()
    {
        // Arrange
        var line = new LogLine
                   {
                       Log = "mysql/error.log",
                       Message = "x",
                       Event = "mariadb.start"
                   };

        // Act
        var json = PayloadRegistry.Serialize(line);
        var again = (LogLine)PayloadRegistry.Deserialize(RecordKind.LogLine, JsonDocument.Parse(json).RootElement)!;

        // Assert
        Assert.AreEqual("mariadb.start", again.Event, "event after a round trip");
        Assert.IsNull(again.Validate(), "the line is still valid");
    }

    #endregion // Methods
}