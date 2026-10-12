using Vandox.Core.Incidents;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BackupPrograms"/>
/// </summary>
[TestClass]
public class BackupProgramsTests
{
    #region Methods

    /// <summary>
    /// Only the exact key <c>pzstd</c> is a backup program.
    /// </summary>
    [TestMethod]
    public void BackupProgramsFindsPzstdOnly()
    {
        // Arrange
        var programs = MemoryAnalysis.Programs(IncidentSamples.Programs(("/usr/bin/pzstd", 10), ("zstd", 10), ("pzstd2", 10), ("PZSTD", 10), ("mariadbd", 10)));
        var others = MemoryAnalysis.Programs(IncidentSamples.Programs(("zstd", 10), ("pzstd2", 10), ("PZSTD", 10)));

        // Act
        var found = BackupPrograms.Find(programs);
        var none = BackupPrograms.Find(others);

        // Assert
        Assert.AreSequenceEqual(["pzstd"], BackupPrograms.Names, "the built-in list");
        Assert.AreSequenceEqual(["pzstd"], found, "pzstd is found");
        Assert.IsEmpty(none, "zstd, pzstd2 and PZSTD are not backup programs");
    }

    #endregion // Methods
}