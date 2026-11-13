using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
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

    /// <summary>
    /// Every external text of the incident page is built with at most 16 runs, and the characters shown are those of the escaped text.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void IncidentDetailViewShowsEveryExternalTextInAtMostSixteenRuns()
    {
        // Arrange
        var text = Alternating(1024);
        var stored = AlternatingIncident(text);
        var counts = new List<int>();

        // Act
        var view = IncidentDetailView.From(stored);

        // Assert
        Assert.AreEqual(1, text[1], "the input holds U+0001");
        Assert.HasCount(1, view.Kills, "one kill");
        Assert.HasCount(1, view.Growth, "one program that grew");
        Assert.HasCount(1, view.Consumers, "one consumer");
        Assert.HasCount(1, view.BackupPrograms, "one backup program");
        Assert.HasCount(1, view.Timeline, "one timeline entry");
        Assert.HasCount(1, view.Timeline[0].Texts, "one text of the entry");
        Assert.HasCount(1, view.PeakProcesses, "one connection command");
        Assert.HasCount(1, view.Extremes, "one extreme");
        Assert.HasCount(1, view.NewTemplates, "one new template");
        Assert.HasCount(1, view.TemplateSpikes, "one template spike");
        CheckField("the kill host", view.Kills[0].Host, text, LogText.MaxShortChars, counts);
        CheckField("the victim", view.Kills[0].Victim, text, LogText.MaxShortChars, counts);
        CheckField("the growth program", view.Growth[0].Program, text, LogText.MaxShortChars, counts);
        CheckField("the consumer", view.Consumers[0].Program, text, LogText.MaxShortChars, counts);
        CheckField("the backup program", view.BackupPrograms[0], text, LogText.MaxShortChars, counts);
        CheckField("the timeline type", view.Timeline[0].Type, text, LogText.MaxShortChars, counts);
        CheckField("the timeline host", view.Timeline[0].Host, text, LogText.MaxShortChars, counts);
        CheckField("the timeline text", view.Timeline[0].Texts[0], $"k: {text}", IncidentsView.MaxTimelineTextChars, counts);
        CheckField("the connection command", view.PeakProcesses[0].Program, text, LogText.MaxShortChars, counts);
        CheckField("the extreme name", view.Extremes[0].Name, text, LogText.MaxShortChars, counts);
        CheckField("the extreme", view.Extremes[0].Extreme, text, LogText.MaxShortChars, counts);
        CheckField("the extreme source", view.Extremes[0].Source, text, LogText.MaxShortChars, counts);

        foreach (var template in view.NewTemplates.Concat(view.TemplateSpikes))
        {
            CheckField("the template source", template.Source, text, LogText.MaxShortChars, counts);
            CheckField("the template program", template.Program, text, LogText.MaxShortChars, counts);
            CheckField("the template", template.Template, text, LogText.MaxShortChars, counts);
        }

        Assert.AreEqual(IncidentsView.MaxTextRuns, counts.Max(), "a text that alternates fills all 16 runs");
        Assert.HasCount(11 + 6, counts, "every field was checked");
    }

    /// <summary>
    /// Builds a stored incident whose every external text is the given one.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The incident</returns>
    private static StoredIncident AlternatingIncident(string text)
    {
        var at = _start.AddMinutes(30);
        var incident = new Incident
                       {
                           Start = _start,
                           End = _start.AddHours(1),
                           FirstKillAt = at,
                           LastKillAt = at,
                           KillCount = 1,
                           Kills = [
                                       new IncidentKill
                                       {
                                           At = at,
                                           Host = text,
                                           VictimCommand = text,
                                           VictimPid = 1
                                       }
                                   ],
                           Timeline = [
                                          new TimelineEntry
                                          {
                                              At = at,
                                              Type = text,
                                              Host = text,
                                              Texts = new Dictionary<string, string>
                                                      {
                                                          ["k"] = text
                                                      }
                                          }
                                      ],
                           Metrics = [
                                         new MetricExtreme
                                         {
                                             Name = text,
                                             Extreme = text,
                                             Source = text,
                                             At = at,
                                             Value = 1,
                                             Samples = 1
                                         }
                                     ],
                           Connections = new ConnectionPeak
                                         {
                                             At = at,
                                             Sockets = 1,
                                             Snapshots = 1,
                                             Processes = [
                                                             new ConnectionProcess
                                                             {
                                                                 Command = text,
                                                                 Count = 1
                                                             }
                                                         ]
                                         },
                           NewTemplates = [
                                              new IncidentNewTemplate
                                              {
                                                  Source = text,
                                                  Program = text,
                                                  Template = text,
                                                  FirstSeen = at,
                                                  Count = 1
                                              }
                                          ],
                           TemplateSpikes = [
                                                new IncidentTemplateSpike
                                                {
                                                    Source = text,
                                                    Program = text,
                                                    Template = text,
                                                    Hour = at,
                                                    Count = 1,
                                                    UsualLevel = 1
                                                }
                                            ]
                       };

        incident.Hypothesis.GrowthKnown = true;
        incident.Hypothesis.Growth.Add(new ProgramGrowth
                                       {
                                           Program = text,
                                           StartRssBytes = 1,
                                           EndRssBytes = 805306369,
                                           GrowthBytes = 805306368
                                       });
        incident.Hypothesis.Consumers.Add(new ProgramUsage
                                          {
                                              Program = text,
                                              RssBytes = 1048576,
                                              Count = 1
                                          });
        incident.Hypothesis.BackupPrograms.Add(text);

        return new StoredIncident
               {
                   Id = 5,
                   Incident = incident,
                   CreatedAt = _start.AddDays(1),
                   UpdatedAt = _start.AddDays(1)
               };
    }

    /// <summary>
    /// Builds a text whose characters alternate between two characters.
    /// </summary>
    /// <param name="length">The length</param>
    /// <returns>The text</returns>
    private static string Alternating(int length)
    {
        var characters = new char[length];

        for (var position = 0; position < length; position++)
        {
            characters[position] = position % 2 == 0 ? 'a' : (char)1;
        }

        return new string(characters);
    }

    /// <summary>
    /// Checks that a built field has at most 16 runs and shows the same characters as the escaped text, and records its run count.
    /// </summary>
    /// <param name="name">The name of the field</param>
    /// <param name="runs">The runs of the field</param>
    /// <param name="text">The text the field was built from</param>
    /// <param name="cap">The character cap of the field</param>
    /// <param name="counts">The collected run counts</param>
    private static void CheckField(string name, IReadOnlyList<TextRun> runs, string text, int cap, List<int> counts)
    {
        Assert.IsLessThanOrEqualTo(IncidentsView.MaxTextRuns, runs.Count, $"{name} has at most 16 runs");
        Assert.AreEqual(LogText.Escape(text, cap), string.Concat(runs.Select(run => run.Text)), $"{name} shows the escaped text unchanged");
        counts.Add(runs.Count);
    }

    #endregion // Methods
}