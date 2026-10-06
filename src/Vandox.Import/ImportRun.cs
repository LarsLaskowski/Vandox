namespace Vandox.Import;

/// <summary>
/// What <see cref="Importer.RunAsync"/> returns: the summary, which covers what was done until the run ended, and the error
/// that stopped it, if any.
/// </summary>
/// <param name="Summary">The summary</param>
/// <param name="Error">The error that stopped the run; <c>null</c> when it finished</param>
public sealed record ImportRun(ImportSummary Summary, Exception? Error);