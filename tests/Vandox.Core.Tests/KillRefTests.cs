using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="KillRef"/>
/// </summary>
[TestClass]
public class KillRefTests
{
    #region Methods

    /// <summary>
    /// A kill with a boot ID, a kernel time of zero or more and a positive victim PID is valid.
    /// </summary>
    /// <param name="json">The kill</param>
    [TestMethod]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":123456789,"victim_pid":4242}""")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":0,"victim_pid":1}""")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":9223372036854775807,"victim_pid":2147483647}""")]
    public void KillRefValidateAcceptsValidKills(string json)
    {
        // Arrange
        var kill = Parse(json);

        // Act
        var error = kill.Validate("trigger.kills[0]");

        // Assert
        Assert.IsNull(error, $"the rules allow {json}, got {error?.Field}: {error?.Reason}");
    }

    /// <summary>
    /// A broken rule names the field below the path of the kill and the reason.
    /// </summary>
    /// <param name="json">The kill</param>
    /// <param name="field">The expected field below the path</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("""{"kernel_time_ns":5,"victim_pid":7}""", "trigger.kills[2].boot_id", "required")]
    [DataRow("""{"boot_id":"","kernel_time_ns":5,"victim_pid":7}""", "trigger.kills[2].boot_id", "required")]
    [DataRow("""{"boot_id":"not-a-uuid","kernel_time_ns":5,"victim_pid":7}""", "trigger.kills[2].boot_id", "invalid characters")]
    [DataRow("""{"boot_id":"0123ABCD-0123-0123-0123-0123456789AB","kernel_time_ns":5,"victim_pid":7}""", "trigger.kills[2].boot_id", "invalid characters")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789a\n","kernel_time_ns":5,"victim_pid":7}""", "trigger.kills[2].boot_id", "invalid characters")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":-1,"victim_pid":7}""", "trigger.kills[2].kernel_time_ns", "must not be negative")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":-9223372036854775808,"victim_pid":7}""", "trigger.kills[2].kernel_time_ns", "must not be negative")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":0}""", "trigger.kills[2].victim_pid", "must be greater than 0")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5}""", "trigger.kills[2].victim_pid", "must be greater than 0")]
    [DataRow("""{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","kernel_time_ns":5,"victim_pid":-4}""", "trigger.kills[2].victim_pid", "must be greater than 0")]
    public void KillRefValidateRules(string json, string field, string reason)
    {
        // Arrange
        var kill = Parse(json);

        // Act
        var error = kill.Validate("trigger.kills[2]");

        // Assert
        Assert.IsNotNull(error, "the kill is invalid");
        Assert.AreEqual(field, error.Field, "field");
        Assert.AreEqual(reason, error.Reason, "reason");
    }

    /// <summary>
    /// The first broken rule is reported, boot ID before kernel time before victim PID.
    /// </summary>
    [TestMethod]
    public void KillRefValidateReportsTheBootIdFirst()
    {
        // Arrange
        var kill = Parse("""{"boot_id":"x","kernel_time_ns":-1,"victim_pid":0}""");

        // Act
        var error = kill.Validate("kill");

        // Assert
        Assert.AreEqual("kill.boot_id", error?.Field, "field of the first broken rule");
    }

    /// <summary>
    /// Reads a kill from JSON.
    /// </summary>
    /// <param name="json">The JSON text</param>
    /// <returns>The kill</returns>
    private static KillRef Parse(string json)
    {
        return JsonSerializer.Deserialize<KillRef>(json)!;
    }

    #endregion // Methods
}