using System.Text;

using Vandox.Backend.Reports;
using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Reports;
using Vandox.Storage;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="MarkdownReportWriter"/>
/// </summary>
[TestClass]
public class MarkdownReportWriterTests
{
    #region Constants

    private const string Backslash = "\\";

    private static readonly string[] _caps = ["1000000", "10000", "100000", "4096"];

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The report of the rich sample is, byte for byte, the report the writer made before it composed its output as blocks: the golden files were made by the unchanged writer.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task MarkdownReportWriterWritesTheGoldenReport(string language)
    {
        // Arrange
        var text = ReportText.For(language);

        Assert.IsNotNull(text, "the language has a table");

        var buffer = new StringWriter();
        var expected = (await File.ReadAllTextAsync(RepositoryFiles.Path($"testdata/report/forensics-{language}.md"), TestContext.CancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal);

        // Act
        await WriteAsync(new MarkdownReportWriter(buffer, text), RichSummary(), RichOutages());

        // Assert
        Assert.AreEqual(expected, buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), "the report equals the golden file");
    }

    /// <summary>
    /// Each part reaches the stream when it is written, even when the buffer of the writer is far larger than the part.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MarkdownReportWriterFlushesEachPart()
    {
        // Arrange
        var outages = RichOutages();
        var stream = new MemoryStream();

        await using var buffer = new StreamWriter(stream, new UTF8Encoding(false), 65536, true);

        var writer = new MarkdownReportWriter(buffer, ReportText.English);
        var lengths = new List<long>();

        // Act
        await writer.WriteSummaryAsync(RichSummary());
        lengths.Add(stream.Length);

        await writer.WriteIncidentAsync(outages[0].Stored, outages[0].Activities);
        lengths.Add(stream.Length);

        await writer.WriteNotesAsync(RichSummary(), outages.Count);
        lengths.Add(stream.Length);

        // Assert
        Assert.IsGreaterThan(0L, lengths[0], "the summary reached the stream");
        Assert.IsGreaterThan(lengths[0], lengths[1], "the outage reached the stream");
        Assert.IsGreaterThan(lengths[1], lengths[2], "the notes reached the stream");
    }

    /// <summary>
    /// Unknown parts of the hypothesis are written as unknown, and a known empty list of growth candidates as none.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterShowsUnknownHypothesisParts()
    {
        // Arrange
        var text = ReportText.English;
        var unknown = Stored(1, Outage(KillAt, 1, "mariadbd"));
        var known = Stored(2, Outage(KillAt, 1, "mariadbd"));

        known.Incident.Hypothesis = new IncidentHypothesis
                                    {
                                        SnapshotCount = 3,
                                        GrowthKnown = true,
                                        Consumers = [
                                                        new ProgramUsage
                                                        {
                                                            Program = "mariadbd",
                                                            RssBytes = 3 * Mib,
                                                            Count = 1
                                                        }
                                                    ]
                                    };

        // Act
        var unknownOutput = await WriteIncidentAsync(text, unknown, Activities(Indicator.Unknown, Indicator.Unknown, Indicator.Unknown));
        var knownOutput = await WriteIncidentAsync(text, known, Activities(Indicator.Yes, Indicator.Yes, Indicator.No, KillAt.AddMinutes(-9)));

        // Assert
        AssertAnyLine(unknownOutput, text.Growth, text.Unknown);
        AssertAnyLine(unknownOutput, text.Consumers, text.Unknown);
        AssertAnyLine(unknownOutput, text.Backup, text.Unknown);
        AssertAnyLine(unknownOutput, text.Boot, text.Unknown);
        AssertAnyLine(unknownOutput, text.PanelHigh, text.Unknown);
        AssertAnyLine(knownOutput, text.Growth, text.None);
        AssertAnyLine(knownOutput, text.Consumers, "` mariadbd `");
        AssertAnyLine(knownOutput, text.Backup, text.Yes);
        AssertAnyLine(knownOutput, text.Boot, "2026-10-02 06:50 UTC");
        AssertAnyLine(knownOutput, text.PanelHigh, text.No);
        Assert.DoesNotContain(line => line.Contains(text.Unknown, StringComparison.Ordinal), Lines(knownOutput, text.Growth), "known growth is not unknown");
        Assert.DoesNotContain(line => line.Contains(text.Unknown, StringComparison.Ordinal), Lines(knownOutput, text.Consumers), "known consumers are not unknown");
    }

    /// <summary>
    /// The victims are listed once each in first-seen order with their count, and kills that are not kept are counted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterCountsVictimsAndKillsNotKept()
    {
        // Arrange
        var text = ReportText.English;
        var incident = Outage(KillAt, 37, "mariadbd");

        incident.Kills = [Victim("mariadbd"), Victim("nginx"), Victim("mariadbd"), Victim(string.Empty), Victim("mariadbd")];
        incident.Hypothesis = new IncidentHypothesis
                              {
                                  SnapshotCount = 1,
                                  GrowthKnown = true,
                                  Consumers = [
                                                  new ProgramUsage
                                                  {
                                                      Program = "web",
                                                      RssBytes = Mib,
                                                      Count = 1
                                                  }
                                              ]
                              };

        var allKept = Outage(KillAt, 2, "mariadbd");

        allKept.Kills = [Victim("mariadbd"), Victim("mariadbd")];
        allKept.Hypothesis = incident.Hypothesis;

        // Act
        var output = await WriteIncidentAsync(text, Stored(1, incident), Activities(Indicator.Yes, Indicator.No, Indicator.Yes));
        var fullOutput = await WriteIncidentAsync(text, Stored(2, allKept), Activities(Indicator.Yes, Indicator.No, Indicator.Yes));
        var mariadbd = output.IndexOf("` mariadbd `", StringComparison.Ordinal);
        var nginx = output.IndexOf("` nginx `", StringComparison.Ordinal);
        var unknown = output.IndexOf(text.Unknown, StringComparison.Ordinal);

        // Assert
        Assert.IsGreaterThan(-1, mariadbd, "the first victim");
        Assert.IsGreaterThan(mariadbd, nginx, "the second victim follows");
        Assert.IsGreaterThan(nginx, unknown, "the kill without a victim is unknown and comes last");
        Assert.AreEqual(output.IndexOf("` mariadbd `", StringComparison.Ordinal), output.LastIndexOf("` mariadbd `", StringComparison.Ordinal), "a victim is listed once");
        Assert.IsTrue(TextChecks.NumberFollows(output, "` mariadbd `", "3"), "mariadbd killed three times");
        Assert.IsTrue(TextChecks.NumberFollows(output, "` nginx `", "1"), "nginx killed once");
        Assert.IsTrue(TextChecks.ContainsNumber(output, "37"), "the kill count");
        AssertAnyLine(output, text.KillsNotKept, "32");
        Assert.DoesNotContain(text.KillsNotKept, fullOutput, "no kill is missing");
    }

    /// <summary>
    /// The notes name the cap that was hit and the number of program entries that were not tracked, and no cap when none was hit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterNamesTheCapsHitInTheNotes()
    {
        // Arrange
        var capped = Summary(Undecided(UnknownReason.TooFewIncidents), Decided(), Decided(), Decided(), snapshotsTruncated: true, bootsTruncated: true, runsTruncated: true, untracked: 17);

        // Act
        var cappedNotes = await WriteNotesAsync(ReportText.English, capped, 13);
        var plainNotes = await WriteNotesAsync(ReportText.English, Summary(Undecided(UnknownReason.TooFewIncidents), Decided(), Decided(), Decided()), 13);

        // Assert
        Assert.IsTrue(TextChecks.ContainsNumber(cappedNotes, "1000000"), "the snapshot cap");
        Assert.IsTrue(TextChecks.ContainsNumber(cappedNotes, "10000"), "the boot cap");
        Assert.IsTrue(TextChecks.ContainsNumber(cappedNotes, "100000"), "the cap of runs of observed hours");
        Assert.IsTrue(TextChecks.ContainsNumber(cappedNotes, "4096"), "the cap of programs");
        Assert.IsTrue(TextChecks.ContainsNumber(cappedNotes, "17"), "the untracked entries");
        Assert.DoesNotContain(number => TextChecks.ContainsNumber(plainNotes, number), _caps, "no cap is named when none was hit");
        Assert.Contains(line => line.StartsWith("## ", StringComparison.Ordinal), plainNotes.Split('\n'), "the notes have their heading");
    }

    /// <summary>
    /// Victim commands and program names are written only as code spans, and no line of a table or heading is made of them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterPutsExternalTextOnlyInCodeSpans()
    {
        // Arrange
        var summary = Summary(Undecided(UnknownReason.TooFewIncidents),
                              Decided(),
                              Decided(),
                              Decided(),
                              permanent: [
                                             new ConsumerSummary
                                             {
                                                 Program = "x|y",
                                                 Snapshots = 10,
                                                 Share = 1,
                                                 MeanRssBytes = Mib,
                                                 PeakRssBytes = 2 * Mib,
                                                 PeakAt = KillAt
                                             }
                                         ],
                              transient: [
                                             new ConsumerSummary
                                             {
                                                 Program = "a`b<script>",
                                                 Snapshots = 1,
                                                 Share = 0.1,
                                                 MeanRssBytes = Mib,
                                                 PeakRssBytes = Mib,
                                                 PeakAt = KillAt
                                             }
                                         ]);
        var incident = Outage(KillAt, 3, "a`b<script>");

        incident.Kills = [Victim("a`b<script>"), Victim("evil\n## injected"), Victim("[x](http://evil) www.evil.example")];
        incident.Hypothesis = new IncidentHypothesis
                              {
                                  SnapshotCount = 1,
                                  GrowthKnown = true,
                                  Growth = [
                                               new ProgramGrowth
                                               {
                                                   Program = "x|y",
                                                   StartRssBytes = Mib,
                                                   EndRssBytes = 2 * Mib,
                                                   GrowthBytes = Mib
                                               }
                                           ],
                                  Consumers = [
                                                  new ProgramUsage
                                                  {
                                                      Program = "x|y",
                                                      RssBytes = Mib,
                                                      Count = 1
                                                  }
                                              ]
                              };

        var buffer = new StringWriter();
        var writer = new MarkdownReportWriter(buffer, ReportText.English);

        // Act
        await writer.WriteSummaryAsync(summary);
        await writer.WriteIncidentAsync(Stored(1, incident), Activities(Indicator.Yes, Indicator.No, Indicator.Yes));
        await writer.WriteNotesAsync(summary, 1);

        var output = buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = output.Split('\n');
        var withoutSpans = output.Replace("`` a`b<script> ``", string.Empty, StringComparison.Ordinal).Replace("` x|y `", string.Empty, StringComparison.Ordinal).Replace(@"` evil\n## injected `", string.Empty, StringComparison.Ordinal).Replace("` [x](http://evil) www.evil.example `", string.Empty, StringComparison.Ordinal);

        // Assert
        Assert.Contains("`` a`b<script> ``", output, "the victim as a code span with a longer fence");
        Assert.Contains("` x|y `", output, "the program as a code span");
        Assert.Contains(@"` evil\n## injected `", output, "the line feed is an escape in one line");
        Assert.Contains("` [x](http://evil) www.evil.example `", output, "links stay literal in the span");
        Assert.DoesNotContain("<script>", withoutSpans, "markup outside a span");
        Assert.DoesNotContain("x|y", withoutSpans, "a table pipe outside a span");
        Assert.DoesNotContain("evil", withoutSpans, "text outside a span");
        Assert.DoesNotContain(line => line.StartsWith("## injected", StringComparison.Ordinal), lines, "no injected heading");
        Assert.DoesNotContain(line => line.StartsWith('|') && (line.Contains("x|y", StringComparison.Ordinal) || line.Contains("<script>", StringComparison.Ordinal)), lines, "no line of external text starts a table row");

        for (var index = 0; index < lines.Length; index++)
        {
            if (StartsTable(lines, index))
            {
                Assert.IsTrue(index > 0 && lines[index - 1].Length == 0, $"a blank line before the table at line {index + 1}");
            }
        }
    }

    /// <summary>
    /// The headings and the fixed words of the report are those of the chosen language and of no other.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterUsesTheHeadingsOfTheLanguage()
    {
        // Arrange
        string[] english = [
                               "# Vandox forensics report",
                               "## Summary",
                               "## Recurring patterns",
                               "### Time of day (UTC)",
                               "### Activities before the outages",
                               "## Memory consumers",
                               "### Permanent consumers",
                               "### Transient consumers",
                               "## Outages",
                               "### Outage #7 at 2026-10-02 06:59 UTC",
                               "## Notes"
                           ];
        string[] german = [
                              "# Vandox-Forensikbericht",
                              "## Übersicht",
                              "## Wiederkehrende Muster",
                              "### Tageszeit (UTC)",
                              "### Aktivitäten vor den Ausfällen",
                              "## Speicherverbraucher",
                              "### Dauerhafte Verbraucher",
                              "### Vorübergehende Verbraucher",
                              "## Ausfälle",
                              "### Ausfall #7 am 2026-10-02 06:59 UTC",
                              "## Hinweise"
                          ];

        // Act
        var englishOutput = await WriteAllAsync(ReportText.English, Mixed());
        var germanOutput = await WriteAllAsync(ReportText.German, Mixed());
        var englishLines = englishOutput.Split('\n');
        var germanLines = germanOutput.Split('\n');

        // Assert
        AssertHeadingsInOrder(englishLines, english);
        AssertHeadingsInOrder(germanLines, german);
        Assert.DoesNotContain(line => english.Contains(line), germanLines, "no English heading in the German report");
        Assert.DoesNotContain(line => german.Contains(line), englishLines, "no German heading in the English report");
        Assert.Contains("unbekannt", germanOutput, "German unknown");
        Assert.Contains("nicht erkannt", germanOutput, "German not detected");
        Assert.IsTrue(TextChecks.ContainsWord(germanOutput, "ja"), "German yes");
        Assert.IsTrue(TextChecks.ContainsWord(germanOutput, "nein"), "German no");
        Assert.DoesNotContain("unknown", germanOutput, "no English word unknown in the German report");
        Assert.DoesNotContain("not detected", germanOutput, "no English words not detected in the German report");
        Assert.IsFalse(TextChecks.ContainsWord(germanOutput, "yes") || TextChecks.ContainsWord(germanOutput, "none"), "no English yes or none in the German report");
        Assert.DoesNotContain("unbekannt", englishOutput, "no German word unbekannt in the English report");
        Assert.DoesNotContain("nicht erkannt", englishOutput, "no German words nicht erkannt in the English report");
        Assert.IsFalse(TextChecks.ContainsWord(englishOutput, "nein"), "no German nein in the English report");
        Assert.IsTrue(germanOutput.EndsWith('\n'), "lines end with a line feed");
    }

    /// <summary>
    /// Shares have one decimal with the separator of the language, memory is whole MiB rounded half away from zero, and growth has a leading plus.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterFormatsNumbersPerLanguage()
    {
        // Arrange
        var summary = Mixed(backup: new PatternResult
                                    {
                                        Outcome = PatternOutcome.Detected,
                                        Matching = 4,
                                        Known = 7,
                                        BaseMatching = 1,
                                        BaseKnown = 16
                                    },
                            panelThreshold: Mib + (Mib / 2),
                            permanent: [
                                           new ConsumerSummary
                                           {
                                               Program = "mariadbd",
                                               Snapshots = 10,
                                               Share = 1,
                                               MeanRssBytes = (2 * Mib) + (Mib / 2),
                                               PeakRssBytes = Mib + (Mib / 2),
                                               PeakAt = KillAt
                                           }
                                       ]);
        var incident = Outage(KillAt, 1, "mariadbd");

        incident.Hypothesis = new IncidentHypothesis
                              {
                                  SnapshotCount = 2,
                                  GrowthKnown = true,
                                  Growth = [
                                               new ProgramGrowth
                                               {
                                                   Program = "mariadbd",
                                                   GrowthBytes = Mib + (Mib / 2)
                                               },
                                               new ProgramGrowth
                                               {
                                                   Program = "nginx",
                                                   GrowthBytes = (2 * Mib) + (Mib / 2)
                                               }
                                           ]
                              };

        // Act
        var english = await WriteAllAsync(ReportText.English, summary, incident);
        var german = await WriteAllAsync(ReportText.German, summary, incident);

        // Assert
        Assert.Contains("57.1 %", english, "English share");
        Assert.Contains("6.3 %", english, "English base share, a half away from zero");
        Assert.DoesNotContain("57,1", english, "no German separator in English");
        Assert.Contains("57,1 %", german, "German share");
        Assert.Contains("6,3 %", german, "German base share");
        Assert.DoesNotContain("57.1", german, "no English separator in German");

        foreach (var output in new[] { english, german })
        {
            Assert.IsTrue(TextChecks.ContainsNumber(output, "2", " MiB") || TextChecks.ContainsNumber(output, "2", "MiB"), "1.5 MiB is 2 MiB");
            Assert.IsTrue(TextChecks.ContainsNumber(output, "3", " MiB") || TextChecks.ContainsNumber(output, "3", "MiB"), "2.5 MiB is 3 MiB");
            Assert.Contains("+2", output, "growth of 1.5 MiB has a leading plus");
            Assert.Contains("+3", output, "growth of 2.5 MiB has a leading plus");
            Assert.DoesNotContain("1.5", output, "no fraction of a MiB");
            Assert.DoesNotContain("2.5", output, "no fraction of a MiB");
        }
    }

    /// <summary>
    /// Times are written as the UTC instant to the minute, whatever offset they carry.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterWritesTimesInUtc()
    {
        // Arrange
        var offset = TimeSpan.FromHours(2);
        var summary = Mixed(generatedAt: new DateTimeOffset(2026, 10, 3, 14, 0, 59, offset), firstLogLineAt: new DateTimeOffset(2026, 9, 30, 3, 15, 30, offset), lookback: TimeSpan.FromMinutes(75));
        var incident = Outage(KillAt, 1, "mariadbd");

        incident.Start = KillAt.AddHours(-1);

        // Act
        var output = await WriteAllAsync(ReportText.English, summary, incident);

        // Assert
        Assert.Contains("### Outage #7 at 2026-10-02 06:59 UTC", output, "first kill, cut to the minute");
        Assert.Contains("2026-10-03 12:00 UTC", output, "generation time");
        Assert.Contains("2026-09-30 01:15 UTC", output, "first log line");
        Assert.Contains("2026-10-02 05:59 UTC", output, "start of the window");
        Assert.DoesNotContain("08:59", output, "no local time");
        Assert.DoesNotContain("14:00", output, "no local generation time");
        Assert.DoesNotContain("+02:00", output, "no offset");
        Assert.IsTrue(TextChecks.ContainsNumber(output, "75"), "the lookback in minutes");
    }

    /// <summary>
    /// The summary has the counts, the first and last outage, a line for each detected pattern with both shares and a line for the unknown rules.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterListsDetectedPatternsInTheSummary()
    {
        // Arrange
        var text = ReportText.English;
        var timeOfDay = Hours(new PatternResult
                              {
                                  Outcome = PatternOutcome.Detected,
                                  Matching = 3,
                                  Known = 3,
                                  BaseMatching = 3,
                                  BaseKnown = 72
                              });
        var summary = Summary(timeOfDay,
                              new PatternResult
                              {
                                  Outcome = PatternOutcome.Detected,
                                  Matching = 4,
                                  Known = 7,
                                  BaseMatching = 1,
                                  BaseKnown = 16
                              },
                              Decided(),
                              new PatternResult
                              {
                                  Outcome = PatternOutcome.Unknown,
                                  Reason = UnknownReason.NoPanelMemory
                              });

        // Act
        var output = await WriteAllAsync(text, summary);
        var section = Between(output, "## Summary", "## Recurring patterns");

        // Assert
        Assert.IsTrue(TextChecks.ContainsNumber(section, "13"), "the number of outages");
        Assert.IsTrue(TextChecks.ContainsNumber(section, "29"), "the number of kills");
        Assert.Contains("2026-09-30 01:05 UTC", section, "first outage");
        Assert.Contains("2026-10-05 22:40 UTC", section, "last outage");
        Assert.Contains("02:00", section, "the detected hour of the day");
        Assert.Contains("100.0 %", section, "share of the outages in that hour");
        Assert.Contains("4.2 %", section, "share of the observed hours in that hour");
        Assert.Contains(text.Backup, section, "the detected activity");
        Assert.Contains("57.1 %", section, "share of the outages after a backup");
        Assert.Contains("6.3 %", section, "share of the observed hours with a backup");
        Assert.Contains(text.PanelHigh, section, "the unknown rule is named");
        Assert.DoesNotContain("No recurring pattern detected", section, "patterns were detected");
    }

    /// <summary>
    /// A rule whose result is unknown is written as unknown with its reason, never as not detected, and the no-pattern sentence names the decided rules.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterNeverWritesUnknownAsNotDetected()
    {
        // Arrange
        var text = ReportText.English;
        var open = Summary(Undecided(UnknownReason.TooFewIncidents),
                           new PatternResult
                           {
                               Outcome = PatternOutcome.Unknown,
                               Reason = UnknownReason.TooManySnapshots
                           },
                           Decided(),
                           new PatternResult
                           {
                               Outcome = PatternOutcome.Unknown,
                               Reason = UnknownReason.NoPanelMemory
                           });
        var closed = Summary(Hours(Decided()), Decided(), Decided(), Decided());

        // Act
        var openOutput = await WriteAllAsync(text, open);
        var closedOutput = await WriteAllAsync(text, closed);
        var openSection = Between(openOutput, "## Summary", "## Recurring patterns");
        var closedSection = Between(closedOutput, "## Summary", "## Recurring patterns");
        var rows = openOutput.Split('\n').Where(line => line.StartsWith('|')).ToList();

        // Assert
        Assert.Contains("No recurring pattern detected among the decided rules", openSection, "some rules are unknown");
        Assert.Contains("No recurring pattern detected", closedSection, "every rule was decided");
        Assert.DoesNotContain("among the decided rules", closedSection, "every rule was decided");
        AssertRows(rows, text.Backup, text.Unknown, text.ReasonTooManySnapshots);
        AssertRows(rows, text.PanelHigh, text.Unknown, text.ReasonNoPanelMemory);
        AssertRows(rows, text.Boot, text.NotDetected);
        Assert.Contains(text.ReasonTooFewIncidents, openOutput, "the time of day gives its reason");
        Assert.DoesNotContain(row => (row.Contains(text.Backup, StringComparison.Ordinal) || row.Contains(text.PanelHigh, StringComparison.Ordinal)) && row.Contains(text.Detected, StringComparison.Ordinal), rows, "an unknown rule is neither detected nor not detected");
    }

    /// <summary>
    /// Every reason a rule can be unknown is written with the text of the language, in the row of the rule.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterWritesTheReasonOfEveryUnknownRule()
    {
        // Arrange
        foreach (var text in new[] { ReportText.English, ReportText.German })
        {
            var summary = Summary(Undecided(UnknownReason.NoBaseData),
                                  Unknown(UnknownReason.TooManySnapshots),
                                  Unknown(UnknownReason.TooManyBoots),
                                  Unknown(UnknownReason.TooManyObservedRuns));
            var noPanel = Summary(Undecided(UnknownReason.NoBaseData), Decided(), Decided(), Unknown(UnknownReason.NoPanelMemory));

            // Act
            var output = await WriteAllAsync(text, summary);
            var panelOutput = await WriteAllAsync(text, noPanel);
            var rows = output.Split('\n').Where(line => line.StartsWith('|')).ToList();
            var panelRows = panelOutput.Split('\n').Where(line => line.StartsWith('|')).ToList();

            // Assert
            Assert.IsGreaterThan(0, text.ReasonNoBaseData.Length, "the reason text exists");
            Assert.Contains(text.ReasonNoBaseData, output, "the time of day without an observed hour");
            AssertRows(rows, text.Backup, text.ReasonTooManySnapshots);
            AssertRows(rows, text.Boot, text.ReasonTooManyBoots);
            AssertRows(rows, text.PanelHigh, text.ReasonTooManyObservedRuns);
            AssertRows(panelRows, text.PanelHigh, text.ReasonNoPanelMemory);
        }
    }

    /// <summary>
    /// An outage with no kept kill lists its victims as unknown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WriterListsAnOutageWithoutKeptKillsAsUnknownVictims()
    {
        // Arrange
        var text = ReportText.English;
        var incident = Outage(KillAt, 5, "mariadbd");

        incident.Kills = [];

        // Act
        var output = await WriteIncidentAsync(text, Stored(1, incident), Activities(Indicator.Yes, Indicator.No, Indicator.Yes));

        // Assert
        AssertAnyLine(output, text.Victims, text.Unknown);
        Assert.DoesNotContain("` mariadbd `", output, "no victim is invented");
    }

    /// <summary>
    /// The section of new listeners stands after the memory consumers and before the outages, with its head, its findings after an agent start and its findings of the agents, in the words of the language.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task MarkdownReportWriterWritesNewListenersBeforeOutages(string language)
    {
        // Arrange
        var english = language == "en";
        var text = english ? ReportText.English : ReportText.German;
        var consumers = english ? "## Memory consumers" : "## Speicherverbraucher";
        var heading = english ? "## New listeners" : "## Neue Listener";
        var outages = english ? "## Outages" : "## Ausfälle";
        string[] expected = english
                                ? [
                                      "- Agent ` web-1 `: 1440 connection snapshots from 2026-09-29 00:05 UTC to 2026-10-06 05:55 UTC; 4 agent starts: 2 compared on all ports, 1 on ports 1 to 1023 only, 1 not compared",
                                      "- Agent ` web-2 `: 12 connection snapshots from 2026-10-03 08:00 UTC to 2026-10-03 09:50 UTC; 0 agent starts: 0 compared on all ports, 0 on ports 1 to 1023 only, 0 not compared",
                                      "- 2026-10-02 06:40 UTC, agent ` web-1 `, after an agent start (compared with 2026-10-02 06:10 UTC), all ports: 2 new",
                                      "  - tcp 0.0.0.0:23, PID 200 ` telnetd `",
                                      "  - tcp6 [::]:8443, no owner",
                                      "- 2026-10-04 03:12 UTC, agent ` web-1 `, after an agent start (compared with 2026-10-04 02:42 UTC), ports 1 to 1023 only: 7 new",
                                      "  - tcp 0.0.0.0:25, PID 311 ` exim4 `",
                                      "  - and 6 more",
                                      "- 2026-10-05 21:00 UTC, agent ` web-1 `, after an agent start, not compared: ports 1 to 1023 not complete",
                                      "- 2026-10-05 12:00 UTC, agent ` web-1 `, found by the agent, all ports: 1 new",
                                      "  - tcp 0.0.0.0:2121, no owner",
                                      "- 2026-10-06 04:00 UTC, agent ` web-2 `, found by the agent, ports 1 to 1023 only: 1 new",
                                      "  - udp 0.0.0.0:69, PID 12 `` tftp`d ``"
                                  ]
                                : [
                                      "- Agent ` web-1 `: 1440 Verbindungs-Snapshots von 2026-09-29 00:05 UTC bis 2026-10-06 05:55 UTC; 4 Agentenstarts: 2 auf allen Ports verglichen, 1 nur auf Ports 1 bis 1023, 1 nicht verglichen",
                                      "- Agent ` web-2 `: 12 Verbindungs-Snapshots von 2026-10-03 08:00 UTC bis 2026-10-03 09:50 UTC; 0 Agentenstarts: 0 auf allen Ports verglichen, 0 nur auf Ports 1 bis 1023, 0 nicht verglichen",
                                      "- 2026-10-02 06:40 UTC, Agent ` web-1 `, nach einem Agentenstart (verglichen mit 2026-10-02 06:10 UTC), alle Ports: 2 neu",
                                      "  - tcp 0.0.0.0:23, PID 200 ` telnetd `",
                                      "  - tcp6 [::]:8443, kein Besitzer",
                                      "- 2026-10-04 03:12 UTC, Agent ` web-1 `, nach einem Agentenstart (verglichen mit 2026-10-04 02:42 UTC), nur Ports 1 bis 1023: 7 neu",
                                      "  - tcp 0.0.0.0:25, PID 311 ` exim4 `",
                                      "  - und 6 weitere",
                                      "- 2026-10-05 21:00 UTC, Agent ` web-1 `, nach einem Agentenstart, nicht verglichen: Ports 1 bis 1023 unvollständig",
                                      "- 2026-10-05 12:00 UTC, Agent ` web-1 `, vom Agenten gefunden, alle Ports: 1 neu",
                                      "  - tcp 0.0.0.0:2121, kein Besitzer",
                                      "- 2026-10-06 04:00 UTC, Agent ` web-2 `, vom Agenten gefunden, nur Ports 1 bis 1023: 1 neu",
                                      "  - udp 0.0.0.0:69, PID 12 `` tftp`d ``"
                                  ];

        // Act
        var output = await WriteSummaryOnlyAsync(text, ListenerSummary(RichListeners()));

        var lines = output.Split('\n');
        var section = Between(output, heading, outages).Split('\n').Skip(1).Where(line => line.Length > 0).ToArray();

        // Assert
        Assert.IsGreaterThan(Array.IndexOf(lines, consumers), Array.IndexOf(lines, heading), "the section follows the memory consumers");
        Assert.IsGreaterThan(Array.IndexOf(lines, heading), Array.IndexOf(lines, outages), "the section precedes the outages");
        Assert.AreSequenceEqual(expected, section, "head, findings after an agent start by agent and sequence number, then the findings of the agents");
    }

    /// <summary>
    /// Without any stored agent snapshot the section says that new listeners are unknown; with snapshots and every start compared it says that none was found; with a start that was not compared it says so with its finding and never claims a plain absence; a listed new listener removes both sentences.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task MarkdownReportWriterWritesNoNewListenerVariants(string language)
    {
        // Arrange
        var english = language == "en";
        var text = english ? ReportText.English : ReportText.German;
        var heading = english ? "## New listeners" : "## Neue Listener";
        var outages = english ? "## Outages" : "## Ausfälle";
        var unknown = english ? "No agent connection snapshot stored: new listeners unknown." : "Kein Verbindungs-Snapshot eines Agenten gespeichert: neue Listener unbekannt.";
        var none = english ? "No new listener found." : "Kein neuer Listener gefunden.";
        var amongCompared = english ? "No new listener found among the compared inventories." : "Kein neuer Listener unter den verglichenen Inventaren gefunden.";
        var notComparedFinding = english
                                     ? "2026-10-05 21:00 UTC, agent ` web-1 `, after an agent start, not compared: no earlier inventory"
                                     : "2026-10-05 21:00 UTC, Agent ` web-1 `, nach einem Agentenstart, nicht verglichen: kein früheres Inventar";
        var at = new DateTimeOffset(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);
        var first = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var noData = await SectionAsync(text, ListenerSummary(new ListenerReport()), heading, outages);
        var allCompared = await SectionAsync(text, ListenerSummary(Section(Tally("web-1", 50, first, at, 2, 2, 0, 0), [], [])), heading, outages);
        var oneNotCompared = await SectionAsync(text, ListenerSummary(Section(Tally("web-1", 50, first, at, 1, 0, 0, 1), [StartFinding("web-1", 9, at, ListenerOutcome.NoEarlierInventory, 0)], [])), heading, outages);
        var withNew = await SectionAsync(text, ListenerSummary(Section(Tally("web-1", 50, first, at, 1, 1, 0, 0), [StartFinding("web-1", 9, at, ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 23, 5, "telnetd"))], [])), heading, outages);

        // Assert
        Assert.IsTrue(HasLine(noData, unknown), "no snapshot: new listeners are unknown");
        Assert.IsFalse(HasLine(noData, none) || noData.Any(line => line.Contains(amongCompared, StringComparison.Ordinal)), "no snapshot: never that none was found");
        Assert.DoesNotContain(line => line.Contains("web-1", StringComparison.Ordinal), noData, "no snapshot: no head line");
        Assert.IsTrue(HasLine(allCompared, none), "every start compared and no finding: none found");
        Assert.DoesNotContain(line => line.Contains(amongCompared, StringComparison.Ordinal) || line.Contains(unknown, StringComparison.Ordinal), allCompared, "every start compared: neither of the other sentences");
        Assert.Contains(line => line.StartsWith("- Agent ` web-1 `:", StringComparison.Ordinal), allCompared, "every start compared: the head names the agent");
        Assert.IsTrue(HasLine(oneNotCompared, notComparedFinding), "a start not compared has its finding");
        Assert.IsTrue(HasLine(oneNotCompared, amongCompared), "a start not compared: none found among the compared inventories");
        Assert.IsFalse(HasLine(oneNotCompared, none), "a start not compared: never the plain sentence");
        Assert.DoesNotContain(line => line.Contains(none, StringComparison.Ordinal) || line.Contains(amongCompared, StringComparison.Ordinal) || line.Contains(unknown, StringComparison.Ordinal), withNew, "a listed new listener: none of the three sentences");
    }

    /// <summary>
    /// A start that was not compared says why with the three reasons of the specification, in the exact words of both languages, and lists no listener and no count.
    /// </summary>
    /// <param name="language">The language</param>
    /// <param name="outcome">The outcome of the comparison</param>
    /// <param name="expected">The expected line without its list marker</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en", ListenerOutcome.NoEarlierInventory, "2026-10-05 21:00 UTC, agent ` web-1 `, after an agent start, not compared: no earlier inventory")]
    [DataRow("en", ListenerOutcome.PrivilegedIncomplete, "2026-10-05 21:00 UTC, agent ` web-1 `, after an agent start, not compared: ports 1 to 1023 not complete")]
    [DataRow("en", ListenerOutcome.NotComparable, "2026-10-05 21:00 UTC, agent ` web-1 `, after an agent start, not compared: inventories not comparable")]
    [DataRow("de", ListenerOutcome.NoEarlierInventory, "2026-10-05 21:00 UTC, Agent ` web-1 `, nach einem Agentenstart, nicht verglichen: kein früheres Inventar")]
    [DataRow("de", ListenerOutcome.PrivilegedIncomplete, "2026-10-05 21:00 UTC, Agent ` web-1 `, nach einem Agentenstart, nicht verglichen: Ports 1 bis 1023 unvollständig")]
    [DataRow("de", ListenerOutcome.NotComparable, "2026-10-05 21:00 UTC, Agent ` web-1 `, nach einem Agentenstart, nicht verglichen: Inventare nicht vergleichbar")]
    public async Task MarkdownReportWriterWritesNotComparedReasons(string language, ListenerOutcome outcome, string expected)
    {
        // Arrange
        var english = language == "en";
        var text = english ? ReportText.English : ReportText.German;
        var at = new DateTimeOffset(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);
        var summary = ListenerSummary(Section(Tally("web-1", 50, at.AddDays(-4), at, 1, 0, 0, 1), [StartFinding("web-1", 9, at, outcome, 0)], []));

        // Act
        var section = await SectionAsync(text, summary, english ? "## New listeners" : "## Neue Listener", english ? "## Outages" : "## Ausfälle");

        // Assert
        var index = Array.IndexOf(section, $"- {expected}");

        Assert.IsGreaterThan(-1, index, "the finding says that the start was not compared and why");
        Assert.IsTrue(index == section.Length - 1 || section[index + 1].Length == 0 || section[index + 1].StartsWith("- ", StringComparison.Ordinal), "no listener is listed below the finding");
    }

    /// <summary>
    /// A finding that omits listeners counts the listed and the omitted ones as new, lists at most the given ones and ends with the line for the rest; a finding that omits none has no such line.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task MarkdownReportWriterWritesRestOfNewListeners(string language)
    {
        // Arrange
        var english = language == "en";
        var text = english ? ReportText.English : ReportText.German;
        var at = new DateTimeOffset(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);
        var listed = Enumerable.Range(0, 64).Select(index => Entry(Proto.Tcp, "0.0.0.0", 1000 + index)).ToArray();
        var summary = ListenerSummary(Section(Tally("web-1", 50, at.AddDays(-4), at, 2, 2, 0, 0),
                                              [
                                                  StartFinding("web-1", 9, at, ListenerOutcome.AllPorts, 6, listed),
                                                  StartFinding("web-1", 12, at.AddHours(1), ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 23))
                                              ],
                                              []));
        var count = english ? "2026-10-05 21:00 UTC, agent ` web-1 `, after an agent start (compared with 2026-10-05 20:30 UTC), all ports: 70 new" : "2026-10-05 21:00 UTC, Agent ` web-1 `, nach einem Agentenstart (verglichen mit 2026-10-05 20:30 UTC), alle Ports: 70 neu";
        var rest = english ? "  - and 6 more" : "  - und 6 weitere";

        // Act
        var section = await SectionAsync(text, summary, english ? "## New listeners" : "## Neue Listener", english ? "## Outages" : "## Ausfälle");

        // Assert
        var head = Array.IndexOf(section, $"- {count}");

        Assert.IsGreaterThan(-1, head, "70 listeners are new: 64 listed and 6 omitted");

        for (var index = 0; index < 64; index++)
        {
            Assert.AreEqual($"  - tcp 0.0.0.0:{1000 + index}, {(english ? "no owner" : "kein Besitzer")}", section[head + 1 + index], $"listener {index} in inventory order");
        }

        Assert.AreEqual(rest, section[head + 65], "the rest follows the 64 listed");
        Assert.ContainsSingle(line => line.Contains(english ? " more" : " weitere", StringComparison.Ordinal), section, "only the finding that omitted listeners has the line for the rest");
    }

    /// <summary>
    /// The agent ID in the head and in all three finding texts, and the command of an owner, are written only as code spans: a command with markup, a run of three backticks, U+202E and a line feed stays one literal line with its escapes, and nothing of the external text stands outside a span.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MarkdownReportWriterWritesCommandAndAgentIdAsCodeSpans()
    {
        // Arrange
        const string agent = "web`1";
        const string agentSpan = "`` web`1 ``";

        var command = "<b>x</b> ``` " + (char)0x202E + "\n";
        var commandSpan = $"```` <b>x</b> ``` {Backslash}u202e{Backslash}n ````";
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var summary = ListenerSummary(Section(Tally(agent, 3, at.AddDays(-1), at.AddDays(1), 2, 1, 0, 1),
                                              [
                                                  StartFinding(agent, 2, at, ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 23, 7, command)),
                                                  StartFinding(agent, 3, at.AddHours(1), ListenerOutcome.NoEarlierInventory, 0)
                                              ],
                                              [AgentFinding(agent, 4, at.AddHours(2), ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 24, 8, command))]));

        Assert.Contains(Backslash, commandSpan, "the expected span still holds the backslash of its escapes");
        Assert.IsTrue(command.Contains((char)0x202E), "the command still holds the right-to-left override");
        Assert.IsTrue(command.Contains('\n'), "the command still holds the line feed");

        // Act
        var section = await SectionAsync(ReportText.English, summary, "## New listeners", "## Outages");

        string[] expected = [
                                $"- Agent {agentSpan}: 3 connection snapshots from 2026-09-30 12:00 UTC to 2026-10-02 12:00 UTC; 2 agent starts: 1 compared on all ports, 0 on ports 1 to 1023 only, 1 not compared",
                                $"- 2026-10-01 12:00 UTC, agent {agentSpan}, after an agent start (compared with 2026-10-01 11:30 UTC), all ports: 1 new",
                                $"  - tcp 0.0.0.0:23, PID 7 {commandSpan}",
                                $"- 2026-10-01 13:00 UTC, agent {agentSpan}, after an agent start, not compared: no earlier inventory",
                                $"- 2026-10-01 14:00 UTC, agent {agentSpan}, found by the agent, all ports: 1 new",
                                $"  - tcp 0.0.0.0:24, PID 8 {commandSpan}"
                            ];

        // Assert
        Assert.AreSequenceEqual(expected, section, "the head and the three finding texts hold the agent ID as a code span, the command stands as one literal line");

        var outside = string.Join('\n', section).Replace(commandSpan, string.Empty, StringComparison.Ordinal).Replace(agentSpan, string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain("web", outside, "the agent ID stands only inside spans");
        Assert.DoesNotContain("<b>", outside, "the markup stands only inside spans");
        Assert.IsFalse(outside.Contains((char)0x202E), "the override stands nowhere");
    }

    /// <summary>
    /// The findings of each kind are listed by agent ID and then sequence number whatever order the store hands them in, and the findings after an agent start come before the findings of the agents.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task MarkdownReportWriterOrdersFindingsByAgentThenSequenceNumber()
    {
        // Arrange
        var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var summary = ListenerSummary(Section(Tally("web-a", 50, day, day.AddDays(1), 3, 0, 0, 3),
                                              [
                                                  StartFinding("web-b", 5, day.AddHours(5), ListenerOutcome.NotComparable, 0),
                                                  StartFinding("web-a", 9, day.AddHours(9), ListenerOutcome.NotComparable, 0),
                                                  StartFinding("web-a", 3, day.AddHours(3), ListenerOutcome.NotComparable, 0)
                                              ],
                                              [
                                                  AgentFinding("web-b", 2, day.AddHours(2), ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 23)),
                                                  AgentFinding("web-a", 8, day.AddHours(8), ListenerOutcome.AllPorts, 0, Entry(Proto.Tcp, "0.0.0.0", 24))
                                              ]));

        // Act
        var section = await SectionAsync(ReportText.English, summary, "## New listeners", "## Outages");

        // Assert
        var order = new[] { "03:00 UTC, agent ` web-a `", "09:00 UTC, agent ` web-a `", "05:00 UTC, agent ` web-b `", "08:00 UTC, agent ` web-a `", "02:00 UTC, agent ` web-b `" };
        var indexes = order.Select(marker => Array.FindIndex(section, line => line.Contains(marker, StringComparison.Ordinal))).ToArray();

        Assert.IsTrue(indexes.All(index => index >= 0), "every finding is listed");
        Assert.AreSequenceEqual(indexes.Order().ToArray(), indexes, "after an agent start by agent then sequence number, then the findings of the agents by agent");
    }

    /// <summary>
    /// The notes name each cap of the section that was hit with the cap and the number left out, in both languages, and name none when no cap was hit.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task MarkdownReportWriterNotesListenerCaps(string language)
    {
        // Arrange
        var english = language == "en";
        var text = english ? ReportText.English : ReportText.German;
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var capped = CappedListeners();
        var starts = capped.StartFindings;
        var found = capped.AgentFindings;
        var startNote = english ? "Only the latest 20 findings after an agent start are listed; 5 earlier ones are left out." : "Nur die letzten 20 Funde nach einem Agentenstart sind aufgeführt; 5 frühere fehlen.";
        var agentNote = english ? "Only the latest 20 findings of the agent are listed; 980 earlier ones are left out." : "Nur die letzten 20 Funde des Agenten sind aufgeführt; 980 frühere fehlen.";
        var agentsNote = english ? "Only the first 64 agent IDs are listed; 3 more are left out." : "Nur die ersten 64 Agenten-IDs sind aufgeführt; 3 weitere fehlen.";
        var onlyAgents = new ListenerReport
                         {
                             Agents = [Tally("web-1", 50, at, at.AddDays(1), 0, 0, 0, 0)],
                             AgentsOmitted = 1,
                             StartFindings = starts,
                             StartFindingsTotal = 20,
                             AgentFindings = found,
                             AgentFindingsTotal = 20
                         };

        // Act
        var cappedNotes = (await WriteNotesAsync(text, ListenerSummary(capped), 13)).Split('\n');
        var agentsOnlyNotes = (await WriteNotesAsync(text, ListenerSummary(onlyAgents), 13)).Split('\n');
        var plainNotes = (await WriteNotesAsync(text, ListenerSummary(Section(Tally("web-1", 50, at, at.AddDays(1), 20, 0, 0, 20), starts, found)), 13)).Split('\n');

        // Assert
        Assert.IsTrue(HasLine(cappedNotes, startNote), "the cap of the findings after an agent start");
        Assert.IsTrue(HasLine(cappedNotes, agentNote), "the cap of the findings of the agents");
        Assert.IsTrue(HasLine(cappedNotes, agentsNote), "the cap of the agent IDs");
        Assert.IsTrue(HasLine(agentsOnlyNotes, english ? "Only the first 64 agent IDs are listed; 1 more are left out." : "Nur die ersten 64 Agenten-IDs sind aufgeführt; 1 weitere fehlen."), "the cap of the agent IDs alone");
        Assert.DoesNotContain(line => line.Contains(english ? "findings" : "Funde", StringComparison.Ordinal), agentsOnlyNotes, "no note of findings when only the agent IDs were capped");
        Assert.DoesNotContain(line => line.Contains(english ? "agent IDs" : "Agenten-IDs", StringComparison.Ordinal), plainNotes, "no note of agent IDs when none was left out");
        Assert.DoesNotContain(line => line.Contains(english ? "findings" : "Funde", StringComparison.Ordinal), plainNotes, "no note of findings when exactly the cap is listed and nothing is left out");
    }

    /// <summary>
    /// Writes only the summary of a report with a new writer.
    /// </summary>
    /// <param name="text">The table</param>
    /// <param name="summary">The summary</param>
    /// <returns>The text with line feeds</returns>
    private static async Task<string> WriteSummaryOnlyAsync(ReportText text, ForensicsSummary summary)
    {
        var buffer = new StringWriter();

        await new MarkdownReportWriter(buffer, text).WriteSummaryAsync(summary);

        return buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the summary and returns the lines of the listener section: the lines between its heading and the next heading, without the empty ones.
    /// </summary>
    /// <param name="text">The table</param>
    /// <param name="summary">The summary</param>
    /// <param name="heading">The heading of the section</param>
    /// <param name="next">The heading that follows it</param>
    /// <returns>The lines</returns>
    private static async Task<string[]> SectionAsync(ReportText text, ForensicsSummary summary, string heading, string next)
    {
        var output = await WriteSummaryOnlyAsync(text, summary);

        return [.. Between(output, heading, next).Split('\n').Skip(1).Where(line => line.Length > 0)];
    }

    /// <summary>
    /// Tells whether a line is the text, as a paragraph or as a list item.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <param name="line">The text without a list marker</param>
    /// <returns><c>true</c> when a line equals the text or the text as a top-level list item</returns>
    private static bool HasLine(string[] lines, string line)
    {
        return lines.Contains(line) || lines.Contains($"- {line}");
    }

    /// <summary>
    /// Tells whether a line is the first row of a table.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <param name="index">The index of the line</param>
    /// <returns><c>true</c> when the line is a table row and the line before it is not</returns>
    private static bool StartsTable(string[] lines, int index)
    {
        if (lines[index].StartsWith('|') && index > 0 && lines[index - 1].StartsWith('|'))
        {
            return false;
        }

        return lines[index].StartsWith('|');
    }

    /// <summary>
    /// Asserts that the headings appear as lines, in the given order.
    /// </summary>
    /// <param name="lines">The lines of the report</param>
    /// <param name="headings">The headings in order</param>
    private static void AssertHeadingsInOrder(string[] lines, string[] headings)
    {
        var previous = -1;

        foreach (var heading in headings)
        {
            var index = Array.IndexOf(lines, heading);

            Assert.IsGreaterThan(previous, index, $"the heading {heading} comes after the previous one");
            previous = index;
        }
    }

    /// <summary>
    /// Asserts that some line holding the label also holds the text.
    /// </summary>
    /// <param name="output">The output</param>
    /// <param name="label">The label of the line</param>
    /// <param name="expected">The text the line holds</param>
    private static void AssertAnyLine(string output, string label, string expected)
    {
        var lines = Lines(output, label);

        Assert.IsNotEmpty(lines, $"a line with {label}");
        Assert.Contains(line => line.Contains(expected, StringComparison.Ordinal) || (expected.Length == 1 && TextChecks.ContainsNumber(line, expected)), lines, $"a line with {label} holds {expected}");
    }

    /// <summary>
    /// Asserts that a table row with the label exists and every such row holds the texts.
    /// </summary>
    /// <param name="rows">The rows of the tables</param>
    /// <param name="label">The label of the row</param>
    /// <param name="expected">The texts the row holds</param>
    private static void AssertRows(List<string> rows, string label, params string[] expected)
    {
        var matching = rows.Where(row => row.Contains(label, StringComparison.Ordinal)).ToList();

        Assert.IsNotEmpty(matching, $"a table row with {label}");

        foreach (var row in matching)
        {
            foreach (var text in expected)
            {
                Assert.Contains(text, row, $"the row of {label} holds {text}");
            }
        }
    }

    /// <summary>
    /// Returns the lines that hold a label.
    /// </summary>
    /// <param name="output">The output</param>
    /// <param name="label">The label</param>
    /// <returns>The lines</returns>
    private static List<string> Lines(string output, string label)
    {
        return output.Split('\n').Where(line => line.Contains(label, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// Returns the text between two headings.
    /// </summary>
    /// <param name="output">The output</param>
    /// <param name="from">The heading that starts the text</param>
    /// <param name="to">The heading that ends the text</param>
    /// <returns>The text</returns>
    private static string Between(string output, string from, string to)
    {
        var start = output.IndexOf(from, StringComparison.Ordinal);
        var end = output.IndexOf(to, start + from.Length, StringComparison.Ordinal);

        Assert.IsGreaterThan(-1, start, $"the heading {from}");
        Assert.IsGreaterThan(start, end, $"the heading {to} after {from}");

        return output[start..end];
    }

    /// <summary>
    /// Writes one incident with a new writer.
    /// </summary>
    /// <param name="text">The table</param>
    /// <param name="stored">The incident</param>
    /// <param name="activities">The activities</param>
    /// <returns>The text with line feeds</returns>
    private static async Task<string> WriteIncidentAsync(ReportText text, StoredIncident stored, IncidentActivities activities)
    {
        var buffer = new StringWriter();

        await new MarkdownReportWriter(buffer, text).WriteIncidentAsync(stored, activities);

        return buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the notes with a new writer.
    /// </summary>
    /// <param name="text">The table</param>
    /// <param name="summary">The summary</param>
    /// <param name="listed">The number of incidents of the second pass</param>
    /// <returns>The text with line feeds</returns>
    private static async Task<string> WriteNotesAsync(ReportText text, ForensicsSummary summary, long listed)
    {
        var buffer = new StringWriter();

        await new MarkdownReportWriter(buffer, text).WriteNotesAsync(summary, listed);

        return buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes a whole report: the summary, the given outages with number 7, and the notes.
    /// </summary>
    /// <param name="text">The table</param>
    /// <param name="summary">The summary</param>
    /// <param name="incidents">The incidents; a default one when none is given</param>
    /// <returns>The text with line feeds</returns>
    private static async Task<string> WriteAllAsync(ReportText text, ForensicsSummary summary, params Incident[] incidents)
    {
        var buffer = new StringWriter();
        var writer = new MarkdownReportWriter(buffer, text);
        var list = incidents.Length == 0 ? [Outage(KillAt, 1, "mariadbd")] : incidents;

        await writer.WriteSummaryAsync(summary);

        foreach (var incident in list)
        {
            await writer.WriteIncidentAsync(Stored(7, incident), Activities(Indicator.Yes, Indicator.No, Indicator.Unknown));
        }

        await writer.WriteNotesAsync(summary, list.Length);

        return buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    #endregion // Methods
}