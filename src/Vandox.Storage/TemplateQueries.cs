namespace Vandox.Storage;

/// <summary>
/// Validates the template queries and builds their SQL statements with bound parameters.
/// </summary>
#pragma warning disable RH2003, S2325, S1172
internal static class TemplateQueries
{
    #region Methods

    /// <summary>
    /// Checks a query for the hourly counts of a template.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(TemplateHourQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks a query for new templates.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(NewTemplateQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks a query for templates above the usual level.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(TemplateSpikeQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the statement for the hourly counts of a template.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateHourQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the statement for new templates.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(NewTemplateQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the statement for templates above the usual level.
    /// </summary>
    /// <param name="query">The validated query</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) Build(TemplateSpikeQuery query)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks the message of a template lookup.
    /// </summary>
    /// <param name="message">The message</param>
    /// <exception cref="InvalidQueryException">The message is longer than the model allows</exception>
    internal static void ValidateMessage(string message)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the statement that reads a template by its key.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="template">The template text</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildFind(string source, string program, string template)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}