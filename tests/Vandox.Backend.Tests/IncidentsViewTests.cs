using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentsView"/>
/// </summary>
[TestClass]
public class IncidentsViewTests
{
    #region Methods

    /// <summary>
    /// The address of a moment is the logs view from 30 seconds before the whole second of the moment to 5 minutes after it, clipped to the first and last whole second of the storable range, and it parses back.
    /// </summary>
    /// <param name="kind">The kind of the moment</param>
    /// <param name="exact">The exact address, or an empty text when only the derived one is compared</param>
    [TestMethod]
    [DataRow("whole-second", "logs?from=2026-10-12T08%3A08%3A40&to=2026-10-12T08%3A14%3A10")]
    [DataRow("fractional-second", "logs?from=2026-10-12T08%3A08%3A40&to=2026-10-12T08%3A14%3A10")]
    [DataRow("near-minimum", "")]
    [DataRow("near-maximum", "")]
    [DataRow("at-minimum", "")]
    [DataRow("at-maximum", "")]
    public void IncidentsViewLogsHrefSpansThirtySecondsBeforeToFiveMinutesAfter(string kind, string exact)
    {
        // Arrange
        var moment = MomentOf(kind);
        var floored = new DateTimeOffset(moment.UtcTicks - (moment.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var firstSecond = new DateTimeOffset(StorableTime.Min.UtcTicks + (TimeSpan.TicksPerSecond - (StorableTime.Min.UtcTicks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);
        var lastSecond = new DateTimeOffset(StorableTime.Max.UtcTicks - (StorableTime.Max.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var expectedFrom = floored - TimeSpan.FromSeconds(30) < firstSecond ? firstSecond : floored - TimeSpan.FromSeconds(30);
        var expectedTo = floored + TimeSpan.FromMinutes(5) > lastSecond ? lastSecond : floored + TimeSpan.FromMinutes(5);

        // Act
        var href = IncidentsView.LogsHref(moment);

        // Assert
        var expected = "logs" + new LogsAddress
                                {
                                    From = expectedFrom,
                                    To = expectedTo
                                }.ToQuery();
        var parsed = LogsAddress.Parse(href["logs".Length..]);

        Assert.StartsWith("logs?", href, "the address is the logs view with a query");
        Assert.AreEqual(expected, href, "from the second before minus 30 seconds to the second plus 5 minutes, clipped to the storable range");
        Assert.IsTrue(exact.Length == 0 || exact == href, "the address in the form of the logs address");
        Assert.AreEqual(string.Empty, parsed.Error, "the address parses back without an error");
        Assert.AreEqual(expectedFrom, parsed.From, "the start parses back");
        Assert.AreEqual(expectedTo, parsed.To, "the end parses back");
    }

    /// <summary>
    /// Builds the moment of a kind.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The moment</returns>
    private static DateTimeOffset MomentOf(string kind)
    {
        return kind switch
               {
                   "whole-second" => new DateTimeOffset(2026, 10, 12, 8, 9, 10, TimeSpan.Zero),
                   "fractional-second" => new DateTimeOffset(2026, 10, 12, 8, 9, 10, TimeSpan.Zero).AddTicks(9870000),
                   "near-minimum" => StorableTime.Min.AddSeconds(10),
                   "near-maximum" => StorableTime.Max.AddSeconds(-10),
                   "at-minimum" => StorableTime.Min,
                   _ => StorableTime.Max
               };
    }

    #endregion // Methods
}