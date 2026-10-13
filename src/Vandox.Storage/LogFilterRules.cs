namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

/// <summary>
/// The rules of the log exploration queries.
/// </summary>
internal static class LogFilterRules
{
    #region Methods

    /// <summary>
    /// Checks a log filter.
    /// </summary>
    /// <param name="filter">The filter</param>
    /// <exception cref="InvalidQueryException">The filter breaks a rule</exception>
    internal static void Validate(LogFilter filter)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks a context query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(LogContextQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks a template list query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(TemplateListQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks a source hour query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(SourceHourQuery query)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}