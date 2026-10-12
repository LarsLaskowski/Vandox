using Vandox.Core.Incidents;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="MemoryAnalysis"/>
/// </summary>
[TestClass]
public class MemoryAnalysisTests
{
    #region Methods

    /// <summary>
    /// A program path is reduced to its last element; a name that does not start with a slash, or has an empty last element, is unchanged.
    /// </summary>
    /// <param name="program">The program as the snapshot names it</param>
    /// <param name="expected">The expected key</param>
    [TestMethod]
    [DataRow("/usr/sbin/mariadbd", "mariadbd")]
    [DataRow("mariadbd", "mariadbd")]
    [DataRow("[kworker/0:1]", "[kworker/0:1]")]
    [DataRow("php-fpm: pool", "php-fpm: pool")]
    [DataRow("/", "/")]
    [DataRow("/usr/bin/", "/usr/bin/")]
    public void MemoryAnalysisNormalizesProgramKeys(string program, string expected)
    {
        // Act
        var key = MemoryAnalysis.ProgramKey(program);

        // Assert
        Assert.AreEqual(expected, key, $"key of {program}");
    }

    /// <summary>
    /// Programs with one key are summed, a snapshot without programs is aggregated from its processes by the first word of the command, and sums saturate.
    /// </summary>
    [TestMethod]
    public void MemoryAnalysisSumsProgramsAndFallsBackToProcesses()
    {
        // Arrange
        var withPrograms = IncidentSamples.Programs(("/usr/bin/pzstd", 100), ("pzstd", 50), ("mariadbd", 700));
        var withProcesses = IncidentSamples.Processes(("/usr/sbin/apache2 -k start", 10), ("apache2", 20), ("apache2 -D FOREGROUND", 30), ("mariadbd", 400), (" odd", 5));
        var saturating = IncidentSamples.Programs(("/usr/bin/pzstd", ulong.MaxValue), ("pzstd", 5));

        // Act
        var fromPrograms = MemoryAnalysis.Programs(withPrograms);
        var fromProcesses = MemoryAnalysis.Programs(withProcesses);
        var saturated = MemoryAnalysis.Programs(saturating);

        // Assert
        Assert.HasCount(2, fromPrograms, "two keys");
        Assert.AreEqual(150UL, fromPrograms["pzstd"].RssBytes, "summed memory of one key");
        Assert.AreEqual(2L, fromPrograms["pzstd"].Count, "summed count of one key");
        Assert.AreEqual("pzstd", fromPrograms["pzstd"].Program, "program name of the key");
        Assert.AreEqual(700UL, fromPrograms["mariadbd"].RssBytes, "memory of the other program");
        Assert.HasCount(3, fromProcesses, "three keys from the processes");
        Assert.AreEqual(60UL, fromProcesses["apache2"].RssBytes, "processes of one first word are summed");
        Assert.AreEqual(3L, fromProcesses["apache2"].Count, "processes of one first word are counted");
        Assert.AreEqual(400UL, fromProcesses["mariadbd"].RssBytes, "a single process");
        Assert.AreEqual(5UL, fromProcesses[" odd"].RssBytes, "a command with an empty first word is its own key");
        Assert.AreEqual(ulong.MaxValue, saturated["pzstd"].RssBytes, "sums saturate");
    }

    /// <summary>
    /// Growth lists programs whose memory grew, largest growth first, ties by key; consumers list the largest programs.
    /// </summary>
    [TestMethod]
    public void MemoryAnalysisRanksGrowthAndConsumers()
    {
        // Arrange
        var first = MemoryAnalysis.Programs(IncidentSamples.Programs(("alpha", 100), ("charlie", 500), ("delta", 100), ("echo", 10), ("foxtrot", 50)));
        var last = MemoryAnalysis.Programs(IncidentSamples.Programs(("alpha", 400), ("bravo", 250), ("charlie", 300), ("delta", 100), ("echo", 160), ("foxtrot", 200)));

        // Act
        var growth = MemoryAnalysis.Growth(first, last, 3);
        var all = MemoryAnalysis.Growth(first, last, 10);
        var largest = MemoryAnalysis.Largest(last, 3);
        var tied = MemoryAnalysis.Largest(MemoryAnalysis.Programs(IncidentSamples.Programs(("b", 5), ("a", 5), ("c", 9))), 3);

        // Assert
        Assert.AreSequenceEqual(["alpha", "bravo", "echo"], growth.Select(entry => entry.Program), "top three growth, the tie of echo and foxtrot goes to the lower key");
        Assert.AreSequenceEqual([300UL, 250UL, 150UL], growth.Select(entry => entry.GrowthBytes), "growth in bytes");
        Assert.AreSequenceEqual([100UL, 0UL, 10UL], growth.Select(entry => entry.StartRssBytes), "memory in the first snapshot, 0 when absent");
        Assert.AreSequenceEqual([400UL, 250UL, 160UL], growth.Select(entry => entry.EndRssBytes), "memory in the last snapshot");
        Assert.AreSequenceEqual(["alpha", "bravo", "echo", "foxtrot"], all.Select(entry => entry.Program), "shrinking and unchanged programs are no candidates");
        Assert.AreSequenceEqual(["alpha", "charlie", "bravo"], largest.Select(entry => entry.Program), "top three consumers");
        Assert.AreSequenceEqual(["c", "a", "b"], tied.Select(entry => entry.Program), "equal memory is ordered by key");
    }

    #endregion // Methods
}