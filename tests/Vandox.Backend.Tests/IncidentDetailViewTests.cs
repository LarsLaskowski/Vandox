using Vandox.Backend.Incidents;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentDetailView"/>
/// </summary>
[TestClass]
public class IncidentDetailViewTests
{
    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// A text of the timeline is cut at 512 characters and any other field at 2,048, and a cut says so.
    /// </summary>
    [TestMethod]
    public void IncidentDetailViewCapsTimelineTexts()
    {
        // Arrange
        var incident = new Incident
                       {
                           Start = _start,
                           End = _start.AddHours(1),
                           FirstKillAt = _start.AddMinutes(30),
                           LastKillAt = _start.AddMinutes(30),
                           KillCount = 1,
                           Kills = [
                                       new IncidentKill
                                       {
                                           At = _start.AddMinutes(30),
                                           Host = new string('k', 3000),
                                           VictimCommand = new string('v', 3000),
                                           VictimPid = 1
                                       }
                                   ],
                           Timeline = [
                                          new TimelineEntry
                                          {
                                              At = _start.AddMinutes(10),
                                              Type = "unit_failed",
                                              Host = new string('z', 3000),
                                              Texts = new Dictionary<string, string>
                                                      {
                                                          ["unit"] = new string('x', 1000)
                                                      }
                                          },
                                          new TimelineEntry
                                          {
                                              At = _start.AddMinutes(20),
                                              Type = new string('w', 3000),
                                              Host = "web-1"
                                          }
                                      ]
                       };
        var stored = new StoredIncident
                     {
                         Id = 3,
                         Incident = incident,
                         CreatedAt = _start.AddDays(1),
                         UpdatedAt = _start.AddDays(1)
                     };

        // Act
        var view = IncidentDetailView.From(stored);

        // Assert
        Assert.AreEqual(3L, view.Id, "the number");
        Assert.HasCount(2, view.Timeline, "both entries are shown");
        Assert.HasCount(1, view.Timeline[0].Texts, "the one text of the first entry");

        var text = view.Timeline[0].Texts[0];
        var textChars = text.Sum(run => run.Text.Count(character => character == 'x'));
        var hostChars = view.Timeline[0].Host.Sum(run => run.Text.Count(character => character == 'z'));
        var typeChars = view.Timeline[1].Type.Sum(run => run.Text.Count(character => character == 'w'));
        var killHostChars = view.Kills[0].Host.Sum(run => run.Text.Count(character => character == 'k'));
        var victimChars = view.Kills[0].Victim.Sum(run => run.Text.Count(character => character == 'v'));

        Assert.IsLessThanOrEqualTo(IncidentsView.MaxTimelineTextChars, textChars, "a timeline text holds at most 512 characters of the value");
        Assert.IsGreaterThanOrEqualTo(400, textChars, "most of the 512 characters are kept");
        Assert.Contains(run => run.Escaped && run.Text.Contains("[cut:", StringComparison.Ordinal), text, "the cut is named in an escaped run");
        Assert.IsLessThanOrEqualTo(2048, hostChars, "a timeline host holds at most 2,048 characters");
        Assert.IsGreaterThanOrEqualTo(2000, hostChars, "most of the 2,048 characters of the host are kept");
        Assert.IsLessThanOrEqualTo(2048, typeChars, "a timeline type holds at most 2,048 characters");
        Assert.IsGreaterThanOrEqualTo(2000, typeChars, "most of the 2,048 characters of the type are kept");
        Assert.IsLessThanOrEqualTo(2048, killHostChars, "a kill host holds at most 2,048 characters");
        Assert.IsLessThanOrEqualTo(2048, victimChars, "a victim command holds at most 2,048 characters");
        Assert.IsGreaterThanOrEqualTo(2000, victimChars, "most of the 2,048 characters of the victim are kept");
    }

    #endregion // Methods
}