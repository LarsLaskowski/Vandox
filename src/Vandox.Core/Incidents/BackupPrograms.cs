namespace Vandox.Core.Incidents;

/// <summary>
/// The names of backup programs that are recognized in process snapshots.
/// </summary>
public static class BackupPrograms
{
    #region Properties

    /// <summary>
    /// Gets the program keys that count as a backup.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = ["pzstd"];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Finds the backup programs among the programs of a snapshot.
    /// </summary>
    /// <param name="programs">The programs by key</param>
    /// <returns>The names found, in list order</returns>
    public static IReadOnlyList<string> Find(IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        return Names.Where(programs.ContainsKey).ToList();
    }

    #endregion // Methods
}