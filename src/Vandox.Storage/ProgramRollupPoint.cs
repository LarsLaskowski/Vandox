namespace Vandox.Storage;

/// <summary>
/// The values of one program in one step.
/// </summary>
/// <param name="Average">The average over the snapshots of the step</param>
/// <param name="Minimum">The smallest value</param>
/// <param name="Maximum">The largest value</param>
public readonly record struct ProgramRollupPoint(double Average, double Minimum, double Maximum);