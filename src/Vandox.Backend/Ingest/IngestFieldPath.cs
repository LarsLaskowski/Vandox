namespace Vandox.Backend.Ingest;

#pragma warning disable RH2003, S2325

/// <summary>
/// Reduces the path of a field error to a form that holds no text of the batch, so it can be logged.
/// </summary>
internal static class IngestFieldPath
{
    #region Methods

    /// <summary>
    /// Turns every quoted map key of a field path into <c>[]</c>. The reader walks each key as the path builder wrote it: the key is quoted,
    /// <c>"</c> is escaped as <c>\"</c> and <c>\</c> as <c>\\</c>, a cut key has <c>...</c> between its closing quote and <c>]</c>, and a
    /// key may hold <c>[</c>, <c>]</c> and <c>"]</c>. It fails closed: a path it cannot parse completely gives no value.
    /// </summary>
    /// <param name="field">The field path of a <c>FieldError</c></param>
    /// <returns>The reduced path; <c>null</c> when the path does not parse, so the caller logs no field</returns>
    internal static string? Reduce(string field)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}