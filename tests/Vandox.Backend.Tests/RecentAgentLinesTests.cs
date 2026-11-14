using Vandox.Backend.Incidents;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="RecentAgentLines"/>
/// </summary>
[TestClass]
public class RecentAgentLinesTests
{
    #region Constants

    private const int LinesInSpan = 13;

    private static readonly DateTimeOffset _start = IncidentSamples.Base;

    private static readonly TimeSpan _spacing = TimeSpan.FromSeconds(10);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Lines read over three hours, each forgetting the lines captured more than 2 minutes before it, never hold more than the lines of
    /// 2 minutes; a line exactly 2 minutes before the last one is still held, one 10 seconds older is not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC13")]
    public void RecentAgentLinesForgetsLinesOlderThanTheSpan()
    {
        // Arrange
        var lines = new RecentAgentLines();
        var count = (int)(TimeSpan.FromHours(3) / _spacing);
        var highest = 0;

        // Act
        for (var index = 1; index <= count; index++)
        {
            var record = Line(index, _start + (_spacing * index), RecordOrigin.Agent);

            lines.Add(record);
            lines.ForgetBefore(record.Record.CapturedAt);
            highest = Math.Max(highest, lines.Count);
        }

        // Assert
        Assert.AreEqual(LinesInSpan, highest, "at most the lines of 2 minutes are held at any time");
        Assert.AreEqual(LinesInSpan, lines.Count, "the lines of the last 2 minutes are held at the end");
        Assert.IsTrue(lines.ContainsAny([count - LinesInSpan + 1]), "the line exactly 2 minutes before the last one is held");
        Assert.IsFalse(lines.ContainsAny([count - LinesInSpan]), "the line 2 minutes and 10 seconds before the last one is forgotten");
        Assert.IsFalse(lines.ContainsAny([1]), "the first line is forgotten");
    }

    /// <summary>
    /// An imported line is not held, while a line of the agent and one of the backend are.
    /// </summary>
    [TestMethod]
    [TestCategory("AC13")]
    public void RecentAgentLinesIgnoresImportedLines()
    {
        // Arrange
        var lines = new RecentAgentLines();

        // Act
        lines.Add(Line(1, _start, RecordOrigin.Import));
        lines.Add(Line(2, _start.AddSeconds(1), RecordOrigin.Agent));
        lines.Add(Line(3, _start.AddSeconds(2), RecordOrigin.Import));
        lines.Add(Line(4, _start.AddSeconds(3), RecordOrigin.Backend));
        lines.ForgetBefore(_start.AddSeconds(3));

        // Assert
        Assert.AreEqual(2, lines.Count, "only the lines that are not imported are held");
        Assert.IsFalse(lines.ContainsAny([1, 3]), "no imported line is held");
        Assert.IsTrue(lines.ContainsAny([2]), "the agent's line is held");
        Assert.IsTrue(lines.ContainsAny([4]), "the backend's line is held");
    }

    /// <summary>
    /// A kill is found as an agent kill when one of its record IDs is a held line, and not when none is, as for a kill of imported lines.
    /// </summary>
    [TestMethod]
    [TestCategory("AC13")]
    public void RecentAgentLinesFindsKillOfAHeldLine()
    {
        // Arrange
        var lines = new RecentAgentLines();

        lines.Add(Line(10, _start, RecordOrigin.Import));
        lines.Add(Line(11, _start.AddSeconds(20), RecordOrigin.Agent));
        lines.ForgetBefore(_start.AddSeconds(20));

        // Act
        var copies = lines.ContainsAny([10, 11]);
        var held = lines.ContainsAny([11]);
        var imported = lines.ContainsAny([10]);
        var unknown = lines.ContainsAny([12, 13]);
        var none = lines.ContainsAny([]);

        // Assert
        Assert.IsTrue(copies, "a kill with an imported and a held copy is an agent kill");
        Assert.IsTrue(held, "a kill of the held line is an agent kill");
        Assert.IsFalse(imported, "a kill of only an imported line is not");
        Assert.IsFalse(unknown, "a kill of lines never added is not");
        Assert.IsFalse(none, "a kill without record IDs is not");
    }

    /// <summary>
    /// Creates a stored log line.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="at">The capture time</param>
    /// <param name="origin">The origin of the record</param>
    /// <returns>The stored record</returns>
    private static StoredRecord Line(long id, DateTimeOffset at, string origin)
    {
        var record = IncidentSamples.Line(at, "cron", "heartbeat ok", "journal", "journal");

        record.Origin = origin;

        return new StoredRecord
               {
                   Id = id,
                   Record = record,
                   AgentId = origin == RecordOrigin.Import ? string.Empty : AgentRecords.AgentId,
                   ReceivedAt = at
               };
    }

    #endregion // Methods
}