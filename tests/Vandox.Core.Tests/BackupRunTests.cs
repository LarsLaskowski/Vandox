using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BackupRun"/>
/// </summary>
[TestClass]
public class BackupRunTests
{
    #region Constants

    /// <summary>
    /// A valid start.
    /// </summary>
    private const string Start = """{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":5150,"command":"pzstd","processes":3}""";

    /// <summary>
    /// A valid end.
    /// </summary>
    private const string End = """{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","gone_by":"2026-10-01T10:05:10Z","max_processes":4}""";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The kind of a backup run is <c>backup_run</c>.
    /// </summary>
    [TestMethod]
    public void BackupRunKindIsBackupRun()
    {
        // Arrange
        var run = Parse(Start);

        // Act
        var kind = run.Kind;

        // Assert
        Assert.AreEqual("backup_run", kind, "kind");
        Assert.AreEqual(RecordKind.BackupRun, kind, "kind constant");
    }

    /// <summary>
    /// Valid starts and ends pass the validation.
    /// </summary>
    /// <param name="json">The payload</param>
    [TestMethod]
    [DataRow(Start)]
    [DataRow(End)]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd","processes":1}""")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:00:00Z","gone_by":"2026-10-01T10:00:10Z","max_processes":1}""")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","gone_by":"2026-10-01T10:05:00Z","max_processes":2}""")]
    public void BackupRunValidateAcceptsValidRuns(string json)
    {
        // Arrange
        var run = Parse(json);

        // Act
        var error = run.Validate();

        // Assert
        Assert.IsNull(error, $"the rules allow {json}, got {error?.Field}: {error?.Reason}");
    }

    /// <summary>
    /// A broken rule names the field and, where the plan fixes it, the reason; an empty reason is not compared.
    /// </summary>
    /// <param name="json">The payload</param>
    /// <param name="field">The expected field</param>
    /// <param name="reason">The expected reason, or empty</param>
    [TestMethod]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd","processes":1}""", "phase", "unknown value")]
    [DataRow("""{"phase":"middle","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd","processes":1}""", "phase", "unknown value")]
    [DataRow("""{"phase":"START","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd","processes":1}""", "phase", "unknown value")]
    [DataRow("""{"phase":"start","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd","processes":1}""", "boot_id", "required")]
    [DataRow("""{"phase":"start","boot_id":"nope","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd","processes":1}""", "boot_id", "invalid characters")]
    [DataRow("""{"phase":"start","boot_id":"0123ABCD-0123-0123-0123-0123456789AB","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd","processes":1}""", "boot_id", "invalid characters")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","pid":1,"command":"pzstd","processes":1}""", "started_at", "required")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T12:00:00+02:00","pid":1,"command":"pzstd","processes":1}""", "started_at", "must be UTC")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","command":"pzstd","processes":1}""", "pid", "must be greater than 0")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":-1,"command":"pzstd","processes":1}""", "pid", "must be greater than 0")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":1,"processes":1}""", "command", "required")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":1,"command":"pzstd"}""", "processes", "")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":5150,"command":"pzstd","processes":3,"last_seen_at":"2026-10-01T10:05:00Z"}""", "last_seen_at", "not allowed for phase start")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":5150,"command":"pzstd","processes":3,"gone_by":"2026-10-01T10:05:10Z"}""", "gone_by", "not allowed for phase start")]
    [DataRow("""{"phase":"start","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","pid":5150,"command":"pzstd","processes":3,"max_processes":2}""", "max_processes", "not allowed for phase start")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","gone_by":"2026-10-01T10:05:10Z","max_processes":4}""", "last_seen_at", "required")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T11:05:00+01:00","gone_by":"2026-10-01T10:05:10Z","max_processes":4}""", "last_seen_at", "must be UTC")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T09:59:59Z","gone_by":"2026-10-01T10:05:10Z","max_processes":4}""", "last_seen_at", "must not be before started_at")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","max_processes":4}""", "gone_by", "required")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","gone_by":"2026-10-01T10:04:59Z","max_processes":4}""", "gone_by", "")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","gone_by":"2026-10-01T10:05:10Z"}""", "max_processes", "")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","gone_by":"2026-10-01T10:05:10Z","max_processes":4,"pid":5150}""", "pid", "not allowed for phase end")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","gone_by":"2026-10-01T10:05:10Z","max_processes":4,"command":"pzstd"}""", "command", "not allowed for phase end")]
    [DataRow("""{"phase":"end","boot_id":"0123abcd-0123-0123-0123-0123456789ab","started_at":"2026-10-01T10:00:00Z","last_seen_at":"2026-10-01T10:05:00Z","gone_by":"2026-10-01T10:05:10Z","max_processes":4,"processes":3}""", "processes", "not allowed for phase end")]
    public void BackupRunValidateRules(string json, string field, string reason)
    {
        // Arrange
        var run = Parse(json);

        // Act
        var error = run.Validate();

        // Assert
        Assert.IsNotNull(error, "the run is invalid");
        Assert.AreEqual(field, error.Field, "field");

        if (reason.Length > 0)
        {
            Assert.AreEqual(reason, error.Reason, "reason");
        }
    }

    /// <summary>
    /// A command longer than the short text limit is refused.
    /// </summary>
    [TestMethod]
    public void BackupRunValidateRefusesTooLongCommand()
    {
        // Arrange
        var run = Parse(Start);

        run.Command = new string('c', ModelLimits.MaxShortTextBytes + 1);

        // Act
        var error = run.Validate();

        // Assert
        Assert.AreEqual("command", error?.Field, "field");
        Assert.AreEqual("too long", error?.Reason, "reason");
    }

    /// <summary>
    /// A start and an end survive the registry's round trip, and each writes only the fields of its phase.
    /// </summary>
    [TestMethod]
    public void BackupRunSurvivesARoundTripWithOnlyThePhaseFields()
    {
        // Arrange
        var start = Parse(Start);
        var end = Parse(End);

        // Act
        var startText = PayloadRegistry.Serialize(start);
        var endText = PayloadRegistry.Serialize(end);
        var startAgain = (BackupRun)PayloadRegistry.Deserialize(RecordKind.BackupRun, JsonDocument.Parse(startText).RootElement)!;
        var endAgain = (BackupRun)PayloadRegistry.Deserialize(RecordKind.BackupRun, JsonDocument.Parse(endText).RootElement)!;

        // Assert
        Assert.AreEqual(5150, startAgain.Pid, "start: pid");
        Assert.AreEqual("pzstd", startAgain.Command, "start: command");
        Assert.AreEqual(3u, startAgain.Processes, "start: processes");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), startAgain.StartedAt, "start: started_at");
        Assert.IsNull(startAgain.LastSeenAt, "start: last_seen_at");
        Assert.IsNull(startAgain.GoneBy, "start: gone_by");
        Assert.AreEqual(0u, startAgain.MaxProcesses, "start: max_processes");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 5, 0, TimeSpan.Zero), endAgain.LastSeenAt, "end: last_seen_at");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 5, 10, TimeSpan.Zero), endAgain.GoneBy, "end: gone_by");
        Assert.AreEqual(4u, endAgain.MaxProcesses, "end: max_processes");
        Assert.AreEqual(0, endAgain.Pid, "end: pid");
        Assert.AreEqual(string.Empty, endAgain.Command, "end: command");
        Assert.DoesNotContain("last_seen_at", startText, "a start writes no last_seen_at");
        Assert.DoesNotContain("gone_by", startText, "a start writes no gone_by");
        Assert.DoesNotContain("max_processes", startText, "a start writes no max_processes");
        Assert.DoesNotContain("\"pid\"", endText, "an end writes no pid");
        Assert.DoesNotContain("\"command\"", endText, "an end writes no command");
        Assert.DoesNotContain("\"processes\"", endText, "an end writes no processes");
        Assert.IsNull(startAgain.Validate(), "the start is valid after a round trip");
        Assert.IsNull(endAgain.Validate(), "the end is valid after a round trip");
    }

    /// <summary>
    /// Reads a backup run from JSON.
    /// </summary>
    /// <param name="json">The JSON text</param>
    /// <returns>The run</returns>
    private static BackupRun Parse(string json)
    {
        return JsonSerializer.Deserialize<BackupRun>(json)!;
    }

    #endregion // Methods
}