namespace Vandox.Backend.Processes;

/// <summary>
/// The figures of one program in a snapshot.
/// </summary>
/// <param name="Rss">The resident memory in bytes</param>
/// <param name="Pss">The proportional memory in bytes; <see langword="null"/> when the agent did not report it</param>
/// <param name="Cpu">The CPU percentage</param>
public readonly record struct ProgramFigure(double Rss, double? Pss, double Cpu);