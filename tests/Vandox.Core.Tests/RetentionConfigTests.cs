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
    /// Every default is a period the parser accepts, with the length the spec names; only the incidents are kept without limit.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void RetentionConfigDefaultsAreValidPeriods()
    {
        // Arrange
        var config = new RetentionConfig();
        (string Key, string Text, TimeSpan? Expected)[] defaults =
        [
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
