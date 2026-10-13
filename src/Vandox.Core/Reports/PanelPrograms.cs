using Vandox.Core.Incidents;

namespace Vandox.Core.Reports;

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
        return program.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sums the RSS of the panel programs, saturating.
    /// </summary>
    /// <param name="programs">The programs of a snapshot</param>
    /// <returns>The summed RSS in bytes</returns>
    public static ulong RssBytes(IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        var sum = 0UL;

        foreach (var (name, usage) in programs)
        {
            if (IsPanel(name))
            {
                sum = ulong.MaxValue - sum < usage.RssBytes ? ulong.MaxValue : sum + usage.RssBytes;
            }
        }

        return sum;
    }

    #endregion // Methods
}