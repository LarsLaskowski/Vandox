using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="FreeSpace"/>
/// </summary>
[TestClass]
public class FreeSpaceTests
{
    #region Methods

    /// <summary>
    /// The probe reads the free space of the file system of a directory: more than nothing and at most the size of the volume.
    /// </summary>
    [TestMethod]
    public void FreeSpaceReadsTheVolumeOfADirectory()
    {
        // Arrange
        using var directory = new TempDirectory();

        // Act
        var available = FreeSpace.Probe(directory.Path);

        // Assert
        Assert.IsNotNull(available, "the free space of an existing directory can be read");
        Assert.IsGreaterThan(0L, available.Value, "free space");
        Assert.IsLessThanOrEqualTo(new DriveInfo(directory.Path).TotalSize, available.Value, "free space is at most the size of the volume");
    }

    /// <summary>
    /// The probe has no value when the path cannot be read.
    /// </summary>
    /// <param name="path">The path</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("bad\0path")]
    public void FreeSpaceProbeHasNoValueForAnUnreadablePath(string path)
    {
        // Act
        var available = FreeSpace.Probe(path);

        // Assert
        Assert.IsNull(available, "no value for an unreadable path");
    }

    /// <summary>
    /// The free space is asked of the probe for the directory of the store, and its answer is passed on, also when it has no value.
    /// </summary>
    /// <param name="answer">The answer of the probe, <c>-1</c> for no value</param>
    [TestMethod]
    [DataRow(12345L)]
    [DataRow(-1L)]
    public void FreeSpaceAvailableAsksTheProbeForItsDirectory(long answer)
    {
        // Arrange
        long? scripted = answer < 0 ? null : answer;
        string? asked = null;
        var space = new FreeSpace("/data/vandox",
                                  directory =>
                                  {
                                      asked = directory;

                                      return scripted;
                                  });

        // Act
        var available = space.Available();

        // Assert
        Assert.AreEqual("/data/vandox", asked, "the directory");
        Assert.AreEqual(scripted, available, "the answer of the probe");
    }

    #endregion // Methods
}