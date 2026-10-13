using Vandox.Core.Incidents;

namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// Recognizes the programs of the hosting panel.
/// </summary>
public static class PanelPrograms
{
    #region Constants

    /// <summary>
    /// The prefix of the panel program names.
    /// </summary>
    public const string Prefix = "sw-engi";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Determines whether a program belongs to the panel.
    /// </summary>
    /// <param name="program">The program key</param>
    /// <returns><c>true</c> for a panel program</returns>
    public static bool IsPanel(string program)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Sums the RSS of the panel programs, saturating.
    /// </summary>
    /// <param name="programs">The programs of a snapshot</param>
    /// <returns>The summed RSS in bytes</returns>
    public static ulong RssBytes(IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}