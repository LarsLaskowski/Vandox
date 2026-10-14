using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// One timed call of the log search measurement: a search, a context or a template list.
/// </summary>
internal sealed class SearchCase
{
    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchCase"/> class.
    /// </summary>
    /// <param name="name">The name in the output</param>
    /// <param name="filter">The filter of a search</param>
    /// <param name="recordId">The record of a context</param>
    /// <param name="templates">The query of a template list</param>
    internal SearchCase(string name, LogFilter? filter, long recordId = 0, TemplateListQuery? templates = null)
    {
        Name = name;
        Filter = filter;
        RecordId = recordId;
        Templates = templates;
    }

    #endregion // Constructor

    #region Properties

    /// <summary>
    /// Gets the name in the output.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the filter of a search.
    /// </summary>
    internal LogFilter? Filter { get; }

    /// <summary>
    /// Gets the record of a context.
    /// </summary>
    internal long RecordId { get; }

    /// <summary>
    /// Gets the query of a template list.
    /// </summary>
    internal TemplateListQuery? Templates { get; }

    #endregion // Properties
}