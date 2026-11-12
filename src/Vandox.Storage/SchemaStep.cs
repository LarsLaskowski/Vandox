using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// One schema step: the statements run in order in one transaction.
/// </summary>
/// <param name="Version">The schema version after this step</param>
/// <param name="Statements">The statements, each setting the text of its own command</param>
internal sealed record SchemaStep(int Version, Action<SqliteCommand>[] Statements);