using System.Text;

using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Writes the forensics report as Markdown. The blocks come from the <see cref="ReportComposer"/>; external text (victim commands and program names) is written
/// only through <see cref="MarkdownText.Code"/>.
/// </summary>
public sealed class MarkdownReportWriter : IReportWriter
{
    #region Fields

    private readonly TextWriter _writer;
    private readonly ReportComposer _composer;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownReportWriter"/> class.
    /// </summary>
    /// <param name="writer">The output</param>
    /// <param name="text">The labels of the language</param>
    public MarkdownReportWriter(TextWriter writer, ReportText text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(text);

        _writer = writer;
        _composer = new ReportComposer(text);
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Writes blocks as Markdown: a blank line ends every block, and a run of list items ends with one, unless the run ends the document.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="blankAfterLastList">Whether a list at the end of the blocks is followed by a blank line; it is not when the blocks end the document</param>
    /// <returns>The Markdown</returns>
    private static string Serialize(IReadOnlyList<ReportBlock> blocks, bool blankAfterLastList)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];

            switch (block.Kind)
            {
                case ReportBlockKind.Heading:
                    {
                        builder.Append('#', block.Level).Append(' ').Append(Inline(block)).Append("\n\n");
                    }

                    break;

                case ReportBlockKind.Paragraph:
                    {
                        builder.Append(Inline(block)).Append("\n\n");
                    }

                    break;

                case ReportBlockKind.Item:
                    {
                        builder.Append(' ', 2 * block.Level).Append("- ").Append(Inline(block)).Append('\n');

                        if (EndsList(blocks, index, blankAfterLastList))
                        {
                            builder.Append('\n');
                        }
                    }

                    break;

                default:
                    {
                        AppendTable(builder, block);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Tells whether a blank line follows a list item.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="index">The index of the item</param>
    /// <param name="blankAfterLastList">Whether a list at the end of the blocks is followed by a blank line</param>
    /// <returns><c>true</c> when the item ends its list and a blank line follows it</returns>
    private static bool EndsList(IReadOnlyList<ReportBlock> blocks, int index, bool blankAfterLastList)
    {
        return index + 1 < blocks.Count ? blocks[index + 1].Kind != ReportBlockKind.Item : blankAfterLastList;
    }

    /// <summary>
    /// Writes the text of a block: plain pieces as they are, external pieces as code spans.
    /// </summary>
    /// <param name="block">The block</param>
    /// <returns>The text</returns>
    private static string Inline(ReportBlock block)
    {
        return string.Concat(block.Pieces.Select(piece => piece.External ? MarkdownText.Code(piece.Text) : piece.Text));
    }

    /// <summary>
    /// Writes a table: the header row, the row of the alignments, the other rows and a blank line.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="table">The table</param>
    private static void AppendTable(StringBuilder builder, ReportBlock table)
    {
        for (var row = 0; row < table.Rows.Count; row++)
        {
            AppendRow(builder, table.Rows[row]);

            if (row == 0)
            {
                AppendRow(builder, table.AlignRight.Select(right => right ? "---:" : "---").ToArray());
            }
        }

        builder.Append('\n');
    }

    /// <summary>
    /// Writes a table row.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="cells">The cells</param>
    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        builder.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
    }

    #endregion // Methods

    #region IReportWriter

    /// <inheritdoc />
    public async Task WriteSummaryAsync(ForensicsSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        await _writer.WriteAsync(Serialize(_composer.Summary(summary), true)).ConfigureAwait(false);
        await _writer.FlushAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(activities);

        await _writer.WriteAsync(Serialize(_composer.Incident(stored, activities), true)).ConfigureAwait(false);
        await _writer.FlushAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteNotesAsync(ForensicsSummary summary, long listed)
    {
        ArgumentNullException.ThrowIfNull(summary);

        await _writer.WriteAsync(Serialize(_composer.Notes(summary, listed), false)).ConfigureAwait(false);
        await _writer.FlushAsync().ConfigureAwait(false);
    }

    #endregion // IReportWriter
}