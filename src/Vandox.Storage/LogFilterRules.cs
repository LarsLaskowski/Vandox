using System.Text;

namespace Vandox.Storage;

/// <summary>
/// The rules of the log exploration queries.
/// </summary>
internal static class LogFilterRules
{
    #region Constants

    /// <summary>
    /// The longest source or program of a filter in UTF-8 bytes.
    /// </summary>
    private const int MaxFilterBytes = 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks a log filter.
    /// </summary>
    /// <param name="filter">The filter</param>
    /// <exception cref="InvalidQueryException">The filter breaks a rule</exception>
    internal static void Validate(LogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(filter.Text);
        ArgumentNullException.ThrowIfNull(filter.Source);
        ArgumentNullException.ThrowIfNull(filter.Program);

        RecordQueries.ValidateWindow(filter.From, filter.To, filter.Limit, StorageLimits.MaxFindLimit);
        RecordQueries.Require(filter.ScanLimit is >= 1 and <= StorageLimits.MaxScanRows, $"scan limit must be between 1 and {StorageLimits.MaxScanRows}");
        ValidateNames(filter.Source, filter.Program);
        RecordQueries.Require(filter.MaxPriority is null or <= 7, "priority must be between 0 and 7");
        RecordQueries.Require(filter.TemplateId >= 0, "template ID must not be negative");

        if (filter.After is not null)
        {
            RecordQueries.Require(StorageTime.InStorableRange(filter.After.CapturedAt), "after must be within the storable range");
            RecordQueries.Require(filter.After.Id > 0, "after id must be positive");
        }

        if (filter.Text.Length > 0)
        {
            FtsQuery.Build(filter.Text);
        }
    }

    /// <summary>
    /// Checks a context query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(LogContextQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        RecordQueries.Require(query.RecordId > 0, "record ID must be positive");
        RecordQueries.Require(query.Before is >= 0 and <= StorageLimits.MaxContextLines && query.After is >= 0 and <= StorageLimits.MaxContextLines, $"context lines must be between 0 and {StorageLimits.MaxContextLines}");
    }

    /// <summary>
    /// Checks a template list query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(TemplateListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Source);
        ArgumentNullException.ThrowIfNull(query.Program);

        RecordQueries.ValidateWindow(query.From, query.To, query.Limit, StorageLimits.MaxTemplateListLimit);
        ValidateNames(query.Source, query.Program);
    }

    /// <summary>
    /// Checks a source hour query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(SourceHourQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Source);

        RecordQueries.ValidateWindow(query.From, query.To, query.Limit);
        RecordQueries.Require(query.Source.Length > 0, "source is required");
        ValidateNames(query.Source, string.Empty);
    }

    /// <summary>
    /// Checks the length of a source and a program.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <exception cref="InvalidQueryException">One of them is too long</exception>
    private static void ValidateNames(string source, string program)
    {
        RecordQueries.Require(Encoding.UTF8.GetByteCount(source) <= MaxFilterBytes, $"source is longer than {MaxFilterBytes} bytes");
        RecordQueries.Require(Encoding.UTF8.GetByteCount(program) <= MaxFilterBytes, $"program is longer than {MaxFilterBytes} bytes");
    }

    #endregion // Methods
}