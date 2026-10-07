namespace Vandox.Import;

/// <summary>
/// Input a parser skipped or a record the store would refuse.
/// </summary>
/// <param name="Line">The 1-based line; 0 when unknown</param>
/// <param name="Reason">A fixed description that never contains input text</param>
public sealed record ImportProblem(long Line, string Reason);