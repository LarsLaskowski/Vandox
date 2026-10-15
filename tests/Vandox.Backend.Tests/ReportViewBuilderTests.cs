using Vandox.Backend.Reports;
using Vandox.Core.Reports;
using Vandox.Storage;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportViewBuilder"/>
/// </summary>
[TestClass]
public class ReportViewBuilderTests
{
    #region Methods

    /// <summary>
    /// A builder keeps only the outages of its page, counts all the outages of the run, and writes the notes of the whole run: with the whole count of the first pass they hold no note about a changed count.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportViewBuilderKeepsOnlyTheOutagesOfThePage()
    {
        // Arrange
        var text = ReportText.English;
        var summary = Summary(Undecided(UnknownReason.TooFewIncidents), Decided(), Decided(), Decided(), incidents: 5);
        var outages = Enumerable.Range(1, 5).Select(id => (Stored(id, Outage(KillAt.AddDays(id), 1, "mariadbd")), Activities(Indicator.Yes, Indicator.No, Indicator.Unknown))).ToArray();
        var builder = new ReportViewBuilder(text, 2, 2);
        var changed = new ReportViewBuilder(text, 2, 2);

        // Act
        await WriteAsync(builder, summary, outages);
        await changed.WriteSummaryAsync(summary);
        await changed.WriteNotesAsync(summary, 4);

        // Assert
        var document = builder.Document;
        var headings = document.Outages.Select(blocks => blocks[0].Pieces[0].Text).ToArray();

        Assert.AreEqual("en", document.Language, "the language of the text");
        Assert.AreEqual(2L, document.FirstOutage, "the first outage kept");
        Assert.AreEqual(5L, document.OutagesListed, "all outages of the run are counted");
        Assert.HasCount(2, document.Outages, "two outages are kept");
        Assert.Contains("#3 ", headings[0], "the third outage is the first of the page");
        Assert.Contains("#4 ", headings[1], "the fourth outage is the last of the page");
        Assert.IsNotEmpty(document.Summary, "the summary is kept");
        Assert.IsNotEmpty(document.Notes, "the notes are kept");
        Assert.DoesNotContain(piece => piece.Text == text.IncidentsChangedNote, document.Notes.SelectMany(block => block.Pieces), "the count of the whole run is compared, not the outages of the page");
        Assert.Contains(piece => piece.Text == text.IncidentsChangedNote, changed.Document.Notes.SelectMany(block => block.Pieces), "a changed count of the whole run is noted");
    }

    /// <summary>
    /// The blocks of the builder are those the Markdown writer composes: the summary, every outage and the notes, for a page that holds them all.
    /// </summary>
    /// <param name="language">The language</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public async Task ReportViewBuilderWritesTheSameBlocksAsTheMarkdownWriter(string language)
    {
        // Arrange
        var text = ReportText.For(language);

        Assert.IsNotNull(text, "the language has a table");

        var summary = RichSummary();
        var outages = RichOutages();
        var composer = new ReportComposer(text);
        var builder = new ReportViewBuilder(text, 0, ReportsView.PageOutages);

        // Act
        await WriteAsync(builder, summary, outages);

        // Assert
        var document = builder.Document;

        Assert.AreEqual(language, document.Language, "the language of the document");
        AssertSameBlocks(composer.Summary(summary), document.Summary, "summary");
        Assert.HasCount(outages.Count, document.Outages, "all outages");

        for (var index = 0; index < outages.Count; index++)
        {
            AssertSameBlocks(composer.Incident(outages[index].Stored, outages[index].Activities), document.Outages[index], $"outage {index}");
        }

        AssertSameBlocks(composer.Notes(summary, outages.Count), document.Notes, "notes");
    }

    /// <summary>
    /// Asserts that two lists of blocks hold the same blocks.
    /// </summary>
    /// <param name="expected">The expected blocks</param>
    /// <param name="actual">The actual blocks</param>
    /// <param name="label">What the blocks are</param>
    private static void AssertSameBlocks(IReadOnlyList<ReportBlock> expected, IReadOnlyList<ReportBlock> actual, string label)
    {
        Assert.IsNotEmpty(expected, $"{label}: the composer made blocks");
        Assert.HasCount(expected.Count, actual, $"{label}: the number of blocks");

        for (var index = 0; index < expected.Count; index++)
        {
            var name = $"{label}, block {index}";

            Assert.AreEqual(expected[index].Kind, actual[index].Kind, $"{name}: kind");
            Assert.AreEqual(expected[index].Level, actual[index].Level, $"{name}: level");
            Assert.AreSequenceEqual(expected[index].Pieces.Select(piece => (piece.Text, piece.External)), actual[index].Pieces.Select(piece => (piece.Text, piece.External)), $"{name}: pieces");
            Assert.AreSequenceEqual(expected[index].AlignRight, actual[index].AlignRight, $"{name}: alignment");
            Assert.HasCount(expected[index].Rows.Count, actual[index].Rows, $"{name}: number of rows");

            for (var row = 0; row < expected[index].Rows.Count; row++)
            {
                Assert.AreSequenceEqual(expected[index].Rows[row], actual[index].Rows[row], $"{name}: row {row}");
            }
        }
    }

    #endregion // Methods
}