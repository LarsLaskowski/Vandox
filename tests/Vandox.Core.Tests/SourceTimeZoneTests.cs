using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SourceTimeZone"/>
/// </summary>
[TestClass]
public class SourceTimeZoneTests
{
    #region Methods

    /// <summary>
    /// Names of the IANA time zone database are found.
    /// </summary>
    /// <param name="name">The name</param>
    [TestMethod]
    [DataRow("UTC")]
    [DataRow("Etc/UTC")]
    [DataRow("Europe/Berlin")]
    [DataRow("America/New_York")]
    [DataRow("Asia/Kolkata")]
    public void SourceTimeZoneFindReturnsZoneOfTheDatabase(string name)
    {
        // Act
        var zone = SourceTimeZone.Find(name);

        // Assert
        Assert.IsNotNull(zone, "the zone is found");
        Assert.AreEqual(name, zone.Id, "the zone has the requested ID");
    }

    /// <summary>
    /// Anything else, including offsets, Windows names and other letter case, is not a zone.
    /// </summary>
    /// <param name="name">The name</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("Europe/Nowhere")]
    [DataRow("+01:00")]
    [DataRow("W. Europe Standard Time")]
    [DataRow("europe/berlin")]
    [DataRow("EUROPE/BERLIN")]
    [DataRow("utc")]
    [DataRow("Europe/Berlin ")]
    [DataRow("../Europe/Berlin")]
    public void SourceTimeZoneFindReturnsNullForOtherNames(string name)
    {
        // Act
        var zone = SourceTimeZone.Find(name);

        // Assert
        Assert.IsNull(zone, "not a zone of the database");
    }

    /// <summary>
    /// Europe/Berlin applies daylight saving time.
    /// </summary>
    [TestMethod]
    public void SourceTimeZoneFindGivesBerlinWithDaylightSavingTime()
    {
        // Act
        var zone = SourceTimeZone.Find("Europe/Berlin");

        // Assert
        Assert.IsNotNull(zone, "the zone is found");
        Assert.AreEqual(2, zone.GetUtcOffset(NodaTime.Instant.FromUtc(2026, 7, 1, 12, 0)).ToTimeSpan().TotalHours, "summer offset");
        Assert.AreEqual(1, zone.GetUtcOffset(NodaTime.Instant.FromUtc(2026, 1, 15, 12, 0)).ToTimeSpan().TotalHours, "winter offset");
    }

    #endregion // Methods
}