using Vandox.Core.Incidents;
using Vandox.Core.Reports;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="PanelPrograms"/>
/// </summary>
[TestClass]
public class PanelProgramsTests
{
    #region Methods

    /// <summary>
    /// Names that start with the ordinal, case-sensitive prefix <c>sw-engi</c> are panel programs.
    /// </summary>
    /// <param name="program">The program name</param>
    /// <param name="expected">Whether it is a panel program</param>
    [TestMethod]
    [DataRow("sw-engi", true)]
    [DataRow("sw-engine", true)]
    [DataRow("sw-engine-fpm", true)]
    [DataRow("sw-cp-server", false)]
    [DataRow("sw-eng", false)]
    [DataRow("Sw-engine", false)]
    [DataRow("xsw-engine", false)]
    [DataRow("", false)]
    public void PanelProgramsMatchesTheEnginePrefix(string program, bool expected)
    {
        // Act
        var actual = PanelPrograms.IsPanel(program);

        // Assert
        Assert.AreEqual(expected, actual, $"IsPanel({program})");
    }

    /// <summary>
    /// The panel memory of a snapshot is the sum of the RSS of all panel programs, saturating at the largest value.
    /// </summary>
    [TestMethod]
    public void PanelProgramsSumsPanelMemory()
    {
        // Arrange
        var programs = Usage(("sw-engine", 100), ("sw-engine-fpm", 20), ("sw-engi", 3), ("sw-cp-server", 1000), ("mariadbd", 5000));
        var huge = Usage(("sw-engine", ulong.MaxValue), ("sw-engine-fpm", 10));

        // Act
        var sum = PanelPrograms.RssBytes(programs);
        var none = PanelPrograms.RssBytes(Usage(("mariadbd", 5000)));
        var saturated = PanelPrograms.RssBytes(huge);

        // Assert
        Assert.AreEqual(123UL, sum, "sum of the panel programs");
        Assert.AreEqual(0UL, none, "no panel program");
        Assert.AreEqual(ulong.MaxValue, saturated, "the sum saturates");
    }

    /// <summary>
    /// Builds the programs of a snapshot by name.
    /// </summary>
    /// <param name="programs">The programs with their resident memory in bytes</param>
    /// <returns>The programs</returns>
    private static Dictionary<string, ProgramUsage> Usage(params (string Program, ulong Rss)[] programs)
    {
        return programs.ToDictionary(entry => entry.Program,
                                     entry => new ProgramUsage
                                              {
                                                  Program = entry.Program,
                                                  RssBytes = entry.Rss,
                                                  Count = 1
                                              },
                                     StringComparer.Ordinal);
    }

    #endregion // Methods
}