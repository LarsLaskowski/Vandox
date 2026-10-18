#pragma warning disable RH2003, S2325

using Vandox.Core.Reports;

namespace Vandox.Storage;

/// <summary>
/// Holds the SQL texts of the listener mark writer and of the two listener reads, validates the query and maps the rows.
/// </summary>
internal static class ListenerQueries
{
    #region Methods

    /// <summary>
    /// Validates a finding query.
    /// </summary>
    /// <param name="query">The query</param>
    internal static void Validate(ListenerFindingQuery query)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}