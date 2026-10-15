using System.Text;

using Vandox.Backend.Reports;
using Vandox.Core.Incidents;
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