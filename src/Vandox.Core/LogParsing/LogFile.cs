namespace Vandox.Core.LogParsing;

/// <summary>
/// Describes the file a parser reads.
/// </summary>
/// <param name="Name">The slash-separated path relative to the import root or inside the archive, cleaned, without a leading slash, and without a trailing <c>.gz</c> when the importer decompressed the file; a label only</param>
/// <param name="ModTime">The modification time of the file or archive entry in UTC; <c>null</c> when unknown</param>
public sealed record LogFile(string Name, DateTimeOffset? ModTime);