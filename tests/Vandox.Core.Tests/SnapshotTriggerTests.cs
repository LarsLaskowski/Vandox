using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SnapshotTrigger"/>
/// </summary>
[TestClass]
public class SnapshotTriggerTests
{
    #region Constants

    /// <summary>
    /// The boot ID of the kills in the tests.
    /// </summary>
    private const string BootId = "0123abcd-0123-0123-0123-0123456789ab";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Triggers of one to 64 distinct kills are valid, and the number of omitted kills is free.
    /// </summary>
    /// <param name="json">The trigger</param>
    [TestMethod]
    [DataRow("""{"kills":[{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":7}]}""")]
    [DataRow("""{"kills":[{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":7},{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":8}],"kills_omitted":3}""")]
    [DataRow("""{"kills":[{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":7},{"boot_id":"0123abcd-0123-0123-0123-0123456789ac","kernel_time_ns":5,"victim_pid":7}]}""")]
    [DataRow("""{"kills":[{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":0,"victim_pid":7}],"kills_omitted":4294967295}""")]
    public void SnapshotTriggerValidateAcceptsValidTriggers(string json)
    {
        // Arrange
        var trigger = Parse(json);

        // Act
        var error = trigger.Validate("trigger");

        // Assert
        Assert.IsNull(error, $"the rules allow {json}, got {error?.Field}: {error?.Reason}");
    }

    /// <summary>
    /// A trigger with 64 distinct kills is valid.
    /// </summary>
    [TestMethod]
    public void SnapshotTriggerValidateAcceptsSixtyFourKills()
    {
        // Arrange
        var trigger = Distinct(64);

        // Act
        var error = trigger.Validate("trigger");

        // Assert
        Assert.IsNull(error, $"64 distinct kills are allowed, got {error?.Field}: {error?.Reason}");
    }

    /// <summary>
    /// A broken rule of a kill names the kill's field below the path of the trigger and the reason.
    /// </summary>
    /// <param name="json">The trigger</param>
    /// <param name="field">The expected field</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("""{"kills":[{"kernel_time_ns":5,"victim_pid":7}]}""", "trigger.kills[0].boot_id", "required")]
    [DataRow("""{"kills":[{"boot_id":"x","kernel_time_ns":5,"victim_pid":7}]}""", "trigger.kills[0].boot_id", "invalid characters")]
    [DataRow("""{"kills":[{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":7},{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":-1,"victim_pid":8}]}""", "trigger.kills[1].kernel_time_ns", "must not be negative")]
    [DataRow("""{"kills":[{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":0}]}""", "trigger.kills[0].victim_pid", "must be greater than 0")]
    [DataRow("""{"kills":[{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":7},{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":6,"victim_pid":-2}]}""", "trigger.kills[1].victim_pid", "must be greater than 0")]
    public void SnapshotTriggerValidateRules(string json, string field, string reason)
    {
        // Arrange
        var trigger = Parse(json);

        // Act
        var error = trigger.Validate("trigger");

        // Assert
        Assert.IsNotNull(error, "the trigger is invalid");
        Assert.AreEqual(field, error.Field, "field");
        Assert.AreEqual(reason, error.Reason, "reason");
    }

    /// <summary>
    /// A trigger without kills, with more than 64 kills or with a kill twice is refused at the list or one of its elements.
    /// </summary>
    /// <param name="what">The case</param>
    [TestMethod]
    [DataRow("no list")]
    [DataRow("empty list")]
    [DataRow("empty list with omitted kills")]
    [DataRow("65 kills")]
    [DataRow("duplicate kill")]
    [DataRow("duplicate kill apart")]
    public void SnapshotTriggerValidateRefusesBrokenLists(string what)
    {
        // Arrange
        var trigger = what switch
                      {
                          "no list" => new SnapshotTrigger(),
                          "empty list" => new SnapshotTrigger
                                          {
                                              Kills = []
                                          },
                          "empty list with omitted kills" => new SnapshotTrigger
                                                             {
                                                                 Kills = [],
                                                                 KillsOmitted = 2
                                                             },
                          "65 kills" => Distinct(65),
                          "duplicate kill" => new SnapshotTrigger
                                              {
                                                  Kills = [Kill(7), Kill(7)]
                                              },
                          _ => new SnapshotTrigger
                               {
                                   Kills = [Kill(7), Kill(8), Kill(7)]
                               }
                      };

        // Act
        var error = trigger.Validate("trigger");

        // Assert
        Assert.IsNotNull(error, $"{what} is refused");
        Assert.IsTrue(error.Field == "trigger.kills" || error.Field.StartsWith("trigger.kills[", StringComparison.Ordinal), $"the field is the list or one of its elements, got {error.Field}");
    }

    /// <summary>
    /// A trigger survives a JSON round trip with its kills and the omitted count.
    /// </summary>
    [TestMethod]
    public void SnapshotTriggerSurvivesARoundTrip()
    {
        // Arrange
        var trigger = new SnapshotTrigger
                      {
                          Kills = [Kill(7), Kill(8)],
                          KillsOmitted = 3
                      };

        // Act
        var again = JsonSerializer.Deserialize<SnapshotTrigger>(JsonSerializer.Serialize(trigger))!;
        var withoutOmitted = JsonSerializer.Serialize(new SnapshotTrigger
                                                      {
                                                          Kills = [Kill(7)]
                                                      });

        // Assert
        Assert.HasCount(2, again.Kills!, "kills");
        Assert.AreEqual(8, again.Kills![1].VictimPid, "victim pid of the second kill");
        Assert.AreEqual(5L, again.Kills[1].KernelTimeNs, "kernel time of the second kill");
        Assert.AreEqual(BootId, again.Kills[1].BootId, "boot ID of the second kill");
        Assert.AreEqual(3u, again.KillsOmitted, "omitted kills");
        Assert.DoesNotContain("kills_omitted", withoutOmitted, "a zero count is left out");
    }

    /// <summary>
    /// Creates a kill of the boot of the tests.
    /// </summary>
    /// <param name="pid">The victim PID</param>
    /// <returns>The kill</returns>
    private static KillRef Kill(int pid)
    {
        return new KillRef
               {
                   BootId = BootId,
                   KernelTimeNs = 5,
                   VictimPid = pid
               };
    }

    /// <summary>
    /// Creates a trigger of distinct kills.
    /// </summary>
    /// <param name="count">The number of kills</param>
    /// <returns>The trigger</returns>
    private static SnapshotTrigger Distinct(int count)
    {
        return new SnapshotTrigger
               {
                   Kills = Enumerable.Range(1, count).Select(Kill).ToList()
               };
    }

    /// <summary>
    /// Reads a trigger from JSON.
    /// </summary>
    /// <param name="json">The JSON text</param>
    /// <returns>The trigger</returns>
    private static SnapshotTrigger Parse(string json)
    {
        return JsonSerializer.Deserialize<SnapshotTrigger>(json)!;
    }

    #endregion // Methods
}