using System.Globalization;
using System.Text;

namespace Vandox.Storage;

/// <summary>
/// The filters of one log search resolved for the finder: the template folded into source, program and search text, and the
/// start of the range moved behind the paging position.
/// </summary>
internal sealed class LogCriteria
{
    #region Properties

    /// <summary>
    /// Gets the search text: the words of the user and of the template, or an empty text.
    /// </summary>
    internal string Text { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the source, or <c>null</c> for any.
    /// </summary>
    internal string? Source { get; private init; }

    /// <summary>
    /// Gets the program, or <c>null</c> for any.
    /// </summary>
    internal string? Program { get; private init; }

    /// <summary>
    /// Gets the highest priority, or <c>null</c> for any.
    /// </summary>
    internal byte? MaxPriority { get; private init; }

    /// <summary>
    /// Gets the first capture time to examine in nanoseconds: the start of the range or the paging position.
    /// </summary>
    internal long StartNs { get; private init; }

    /// <summary>
    /// Gets the end of the range in nanoseconds, exclusive.
    /// </summary>
    internal long ToNs { get; private init; }

    /// <summary>
    /// Gets the capture time of the paging position in nanoseconds, or <c>null</c> for the first page.
    /// </summary>
    internal long? AfterNs { get; private init; }

    /// <summary>
    /// Gets the record ID of the paging position.
    /// </summary>
    internal long AfterId { get; private init; }

    /// <summary>
    /// Gets the page size.
    /// </summary>
    internal int Limit { get; private init; }

    /// <summary>
    /// Gets the template the lines must normalize to, or <c>null</c>.
    /// </summary>
    internal LogTemplate? Template { get; private init; }

    /// <summary>
    /// Gets the user's search text without the words of a template; the scan tests only this text.
    /// </summary>
    internal string ScanText { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the number of lines a scan examines at most.
    /// </summary>
    internal int ExaminedLines { get; private init; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Resolves a validated filter.
    /// </summary>
    /// <param name="filter">The filter</param>
    /// <param name="template">The template of the filter, or <c>null</c></param>
    /// <returns>The criteria, or <c>null</c> when the template belongs to another source or program than the filter names</returns>
    internal static LogCriteria? Create(LogFilter filter, LogTemplate? template)
    {
        if (template is not null && (Contradicts(filter.Source, template.Source) || Contradicts(filter.Program, template.Program)))
        {
            return null;
        }

        var from = StorageTime.ToNanoseconds(filter.From);
        var after = filter.After is null ? (long?)null : StorageTime.ToNanoseconds(filter.After.CapturedAt);

        var scanText = string.Join(' ', Split(filter.Text));

        return new LogCriteria
               {
                   Text = Combine(filter.Text, template),
                   ScanText = scanText,
                   ExaminedLines = LogFinder.ExaminedLines(filter.ScanLimit, FtsQuery.CountTokens(scanText)),
                   Source = template?.Source ?? Optional(filter.Source),
                   Program = template?.Program ?? Optional(filter.Program),
                   MaxPriority = filter.MaxPriority,
                   StartNs = after is null ? from : Math.Max(from, after.Value),
                   ToNs = StorageTime.ToNanoseconds(filter.To),
                   AfterNs = after,
                   AfterId = filter.After?.Id ?? 0,
                   Limit = filter.Limit,
                   Template = template
               };
    }

    /// <summary>
    /// Tells whether a filter value names something else than the template.
    /// </summary>
    /// <param name="filterValue">The value of the filter, empty for any</param>
    /// <param name="templateValue">The value of the template</param>
    /// <returns><c>true</c> when no line can satisfy both</returns>
    private static bool Contradicts(string filterValue, string templateValue)
    {
        return filterValue.Length > 0 && filterValue != templateValue;
    }

    /// <summary>
    /// Turns an empty filter value into <c>null</c>.
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The value, or <c>null</c> when it is empty</returns>
    private static string? Optional(string value)
    {
        return value.Length > 0 ? value : null;
    }

    /// <summary>
    /// Joins the words of the user and the words of the template, keeping the rules of the search text: the template words are cut first.
    /// </summary>
    /// <param name="text">The text of the user</param>
    /// <param name="template">The template, or <c>null</c></param>
    /// <returns>The search text</returns>
    private static string Combine(string text, LogTemplate? template)
    {
        if (template is null)
        {
            return text;
        }

        var terms = Split(text);
        var bytes = Encoding.UTF8.GetByteCount(string.Join(' ', terms));

        foreach (var term in Split(TemplateTerms.SearchText(template.Template)).Where(HasLetterOrDigit))
        {
            var needed = Encoding.UTF8.GetByteCount(term) + (terms.Count > 0 ? 1 : 0);

            if (terms.Count >= StorageLimits.MaxSearchTerms || bytes + needed > StorageLimits.MaxSearchBytes)
            {
                break;
            }

            terms.Add(term);
            bytes += needed;
        }

        return string.Join(' ', terms);
    }

    /// <summary>
    /// Splits a text at white space.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The terms</returns>
    private static List<string> Split(string text)
    {
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>
    /// Tells whether a term holds a letter or a decimal digit.
    /// </summary>
    /// <param name="term">The term</param>
    /// <returns><c>true</c> when it does</returns>
    private static bool HasLetterOrDigit(string term)
    {
        return term.EnumerateRunes().Any(rune => Rune.IsLetter(rune) || CharUnicodeInfo.GetUnicodeCategory(rune.Value) == UnicodeCategory.DecimalDigitNumber);
    }

    #endregion // Methods
}