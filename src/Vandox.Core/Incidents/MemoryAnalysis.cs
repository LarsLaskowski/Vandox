using Vandox.Core.Model;

namespace Vandox.Core.Incidents;

#pragma warning disable RH2003, S2325

/// <summary>
/// Reads the memory use of programs from process snapshots.
/// </summary>
public static class MemoryAnalysis
{
    #region Methods

    /// <summary>
    /// Normalizes a program name to its key.
    /// </summary>
    /// <param name="program">The program name</param>
    /// <returns>The key</returns>
    public static string ProgramKey(string program)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the programs of a snapshot by key.
    /// </summary>
    /// <param name="snapshot">The snapshot</param>
    /// <returns>The programs</returns>
    public static IReadOnlyDictionary<string, ProgramUsage> Programs(ProcessSnapshot snapshot)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Ranks the programs whose RSS grew.
    /// </summary>
    /// <param name="first">The programs of the first snapshot</param>
    /// <param name="last">The programs of the last snapshot</param>
    /// <param name="count">The most candidates</param>
    /// <returns>The candidates</returns>
    public static IReadOnlyList<ProgramGrowth> Growth(IReadOnlyDictionary<string, ProgramUsage> first, IReadOnlyDictionary<string, ProgramUsage> last, int count)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the programs with the largest RSS.
    /// </summary>
    /// <param name="programs">The programs</param>
    /// <param name="count">The most programs</param>
    /// <returns>The programs</returns>
    public static IReadOnlyList<ProgramUsage> Largest(IReadOnlyDictionary<string, ProgramUsage> programs, int count)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}