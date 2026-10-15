using Microsoft.AspNetCore.Components;

using Vandox.Backend.Components.Logs;
using Vandox.Backend.Logs;
using Vandox.Backend.Reports;

namespace Vandox.Backend.Components.Reports;

/// <summary>
/// Shows a report document as headings, paragraphs, lists and tables. External text is shown only as text of an <see cref="ExternalText"/> inside a <c>code</c>
/// element.
/// </summary>
public sealed partial class ReportContent : ComponentBase
{
    #region Properties

    /// <summary>
    /// Gets or sets the document to show.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public ReportDocument Document { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gets the style class of a table cell.
    /// </summary>
    /// <param name="table">The table</param>
    /// <param name="column">The column</param>
    /// <returns>The class of a number column, otherwise <c>null</c>, so that no attribute is written</returns>
    private static string? CellClass(ReportBlock table, int column)
    {
        return column < table.AlignRight.Count && table.AlignRight[column] ? "num" : null;
    }

    /// <summary>
    /// Splits the items of a run into the entries of a list: an item of level 0 with the items of level 1 that follow it.
    /// </summary>
    /// <param name="run">The items</param>
    /// <returns>The entries</returns>
    private static IEnumerable<(ReportBlock Item, IReadOnlyList<ReportBlock> Nested)> Entries(IReadOnlyList<ReportBlock> run)
    {
        var entries = new List<(ReportBlock Item, List<ReportBlock> Nested)>();

        foreach (var item in run)
        {
            if (item.Level > 0 && entries.Count > 0)
            {
                entries[^1].Nested.Add(item);
            }
            else
            {
                entries.Add((item, []));
            }
        }

        return entries.Select(entry => (entry.Item, (IReadOnlyList<ReportBlock>)entry.Nested));
    }

    /// <summary>
    /// Writes the text of a block: plain pieces as text, external pieces as an <see cref="ExternalText"/> inside a <c>code</c> element.
    /// </summary>
    /// <param name="block">The block</param>
    /// <returns>The content</returns>
    private static RenderFragment Text(ReportBlock block)
    {
        return builder =>
               {
                   foreach (var piece in block.Pieces)
                   {
                       if (piece.External)
                       {
                           builder.OpenElement(0, "code");
                           builder.OpenComponent<ExternalText>(1);
                           builder.AddComponentParameter(2, nameof(ExternalText.Runs), LogText.Runs(piece.Text, LogText.MaxShortChars));
                           builder.CloseComponent();
                           builder.CloseElement();
                       }
                       else
                       {
                           builder.AddContent(3, piece.Text);
                       }
                   }
               };
    }

    /// <summary>
    /// Writes a heading of its level.
    /// </summary>
    /// <param name="block">The heading</param>
    /// <returns>The element</returns>
    private static RenderFragment Heading(ReportBlock block)
    {
        return builder =>
               {
                   var tag = block.Level switch
                             {
                                 1 => "h1",
                                 2 => "h2",
                                 _ => "h3"
                             };

                   builder.OpenElement(0, tag);
                   builder.AddContent(1, Text(block));
                   builder.CloseElement();
               };
    }

    /// <summary>
    /// Splits a list of blocks into groups.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <returns>The groups</returns>
    private static IEnumerable<IReadOnlyList<ReportBlock>> Split(IReadOnlyList<ReportBlock> blocks)
    {
        var groups = new List<List<ReportBlock>>();

        foreach (var block in blocks)
        {
            if (block.Kind == ReportBlockKind.Item && groups.Count > 0 && groups[^1][0].Kind == ReportBlockKind.Item)
            {
                groups[^1].Add(block);
            }
            else
            {
                groups.Add([block]);
            }
        }

        return groups;
    }

    /// <summary>
    /// Splits the document into groups: each block that is no list item alone, and each run of list items. The summary, each outage and the notes are
    /// split apart, so that a list never runs from one into the next.
    /// </summary>
    /// <returns>The groups</returns>
    private IEnumerable<IReadOnlyList<ReportBlock>> Groups()
    {
        var lists = new List<IReadOnlyList<ReportBlock>>
                    {
                        Document.Summary
                    };

        lists.AddRange(Document.Outages);
        lists.Add(Document.Notes);

        return lists.SelectMany(Split);
    }

    #endregion // Methods
}