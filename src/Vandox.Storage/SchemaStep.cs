namespace Vandox.Storage;

/// <summary>
/// One schema step: the statements run in order in one transaction.
/// </summary>
/// <param name="Version">The schema version after this step</param>
/// <param name="Statements">The statements</param>
internal sealed record SchemaStep(int Version, string[] Statements);