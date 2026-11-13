using Vandox.Core.Configuration;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="RetentionConfig"/>
/// </summary>
[TestClass]
public class RetentionConfigTests
{
    #region Methods

    /// <summary>
    /// A new configuration holds the default texts the spec names for the seven retention keys.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void RetentionConfigDefaultsMatchTheSpec()
    {
        // Act
        var config = new RetentionConfig();

        // Assert
        Assert.AreEqual("30d", config.Raw, "default of retention.raw");
        Assert.AreEqual("365d", config.FiveMinuteRollups, "default of retention.rollups_5m");
        Assert.AreEqual("1095d", config.HourlyRollups, "default of retention.rollups_1h");
        Assert.AreEqual("90d", config.Logs, "default of retention.logs");
        Assert.AreEqual("30d", config.Snapshots, "default of retention.snapshots");
        Assert.AreEqual("1095d", config.Events, "default of retention.events");
        Assert.AreEqual("unlimited", config.Incidents, "default of retention.incidents");
        Assert.AreEqual(RetentionPeriod.Unlimited, config.Incidents, "the incident default is the unlimited keyword");
    }

    /// <summary>
    /// Every default is a period the parser accepts, with the length the spec names; only the incidents are kept without limit.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void RetentionConfigDefaultsAreValidPeriods()
    {
        // Arrange
        var config = new RetentionConfig();
        (string Key, string Text, TimeSpan? Expected)[] defaults = [
                                                                       ("raw", config.Raw, TimeSpan.FromDays(30)),
                                                                       ("rollups_5m", config.FiveMinuteRollups, TimeSpan.FromDays(365)),
                                                                       ("rollups_1h", config.HourlyRollups, TimeSpan.FromDays(1095)),
                                                                       ("logs", config.Logs, TimeSpan.FromDays(90)),
                                                                       ("snapshots", config.Snapshots, TimeSpan.FromDays(30)),
                                                                       ("events", config.Events, TimeSpan.FromDays(1095)),
                                                                       ("incidents", config.Incidents, null)
                                                                   ];

        // Act
        var parsed = defaults.Select(entry => (entry.Key, Accepted: RetentionPeriod.TryParse(entry.Text, out var value), Value: value, entry.Expected)).ToList();

        // Assert
        foreach (var entry in parsed)
        {
            Assert.IsTrue(entry.Accepted, $"the default of retention.{entry.Key} is accepted");
            Assert.AreEqual(entry.Expected, entry.Value, $"period of the default of retention.{entry.Key}");
        }
    }

    #endregion // Methods
}