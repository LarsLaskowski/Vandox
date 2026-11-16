namespace Vandox.Storage;

/// <summary>
/// The statements of the history reads. Every statement is a constant; values are bound as parameters.
/// </summary>
internal static class HistoryQueries
{
    #region Constants

    /// <summary>
    /// The operation named in the error of a history read.
    /// </summary>
    internal const string Context = "store: reading history";

    #endregion // Constants
}