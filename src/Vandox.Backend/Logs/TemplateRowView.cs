using Vandox.Storage;

namespace Vandox.Backend.Logs;

/// <summary>
/// A template prepared for display: its external text is already escaped and capped.
/// </summary>
public sealed class TemplateRowView
{
    #region Properties

    /// <summary>
    /// Gets or sets the template ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the template text.
    /// </summary>
    public IReadOnlyList<TextRun> Text { get; set; } = [];

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    public IReadOnlyList<TextRun> Source { get; set; } = [];

    /// <summary>
    /// Gets or sets the program.
    /// </summary>
    public IReadOnlyList<TextRun> Program { get; set; } = [];

    /// <summary>
    /// Gets or sets the capture time of the earliest line.
    /// </summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>
    /// Gets or sets the capture time of the latest line.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// Gets or sets the number of lines over the whole time.
    /// </summary>
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the number of lines in the hours of the range.
    /// </summary>
    public long CountInRange { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the view of a template.
    /// </summary>
    /// <param name="template">The template</param>
    /// <param name="countInRange">The number of lines in the range</param>
    /// <returns>The view</returns>
    public static TemplateRowView From(LogTemplate template, long countInRange)
    {
        ArgumentNullException.ThrowIfNull(template);

        return new TemplateRowView
               {
                   Id = template.Id,
                   Text = LogText.Runs(template.Template, LogText.MaxShortChars),
                   Source = LogText.Runs(template.Source, LogText.MaxShortChars),
                   Program = LogText.Runs(template.Program, LogText.MaxShortChars),
                   FirstSeen = template.FirstSeen,
                   LastSeen = template.LastSeen,
                   Count = template.Count,
                   CountInRange = countInRange
               };
    }

    #endregion // Methods
}