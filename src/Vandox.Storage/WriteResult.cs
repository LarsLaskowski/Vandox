namespace Vandox.Storage;

/// <summary>
/// Counts the records of a batch that were stored and those that were already present.
/// </summary>
/// <param name="Stored">The number of records stored</param>
/// <param name="Duplicates">The number of records that were already present</param>
public readonly record struct WriteResult(int Stored, int Duplicates);