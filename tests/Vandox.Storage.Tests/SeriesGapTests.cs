namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the mapping of a gap's collector to the lines it concerns in <see cref="SeriesGap"/>
/// </summary>
[TestClass]
public class SeriesGapTests
{
    #region Methods

    /// <summary>
    /// A gap concerns the lines its collector writes; a collector that writes no metric concerns none, and a gap without a collector or of an unnamed collector concerns every line.
    /// </summary>
    /// <param name="collector">The collector of the gap</param>
    /// <param name="source">The source of the line</param>
    /// <param name="name">The name of the line</param>
    /// <param name="expected">Whether the gap concerns the line</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("self", "agent", "agent_rss_bytes", true)]
    [DataRow("self", "system", "mem_available_bytes", false)]
    [DataRow("self", "mariadb", "mariadb_threads_connected", false)]
    [DataRow("filesystem", "system", "filesystem_used_bytes", true)]
    [DataRow("filesystem", "system", "mem_available_bytes", false)]
    [DataRow("filesystem", "system", "clock_offset_seconds", false)]
    [DataRow("filesystem", "agent", "filesystem_used_bytes", false)]
    [DataRow("clock", "system", "clock_offset_seconds", true)]
    [DataRow("clock", "agent", "clock_offset_seconds", false)]
    [DataRow("clock", "system", "cpu_percent", false)]
    [DataRow("clock", "system", "filesystem_used_bytes", false)]
    [DataRow("system", "system", "cpu_percent", true)]
    [DataRow("system", "system", "mem_available_bytes", true)]
    [DataRow("system", "system", "filesystem_used_bytes", false)]
    [DataRow("system", "system", "clock_offset_seconds", false)]
    [DataRow("system", "agent", "agent_rss_bytes", false)]
    [DataRow("mariadb", "mariadb", "mariadb_threads_connected", true)]
    [DataRow("mariadb", "system", "cpu_percent", false)]
    [DataRow("logs", "system", "cpu_percent", false)]
    [DataRow("logs", "agent", "agent_rss_bytes", false)]
    [DataRow("kernel", "system", "cpu_percent", false)]
    [DataRow("processes", "system", "cpu_percent", false)]
    [DataRow("connections", "system", "cpu_percent", false)]
    [DataRow("systemd", "system", "cpu_percent", false)]
    [DataRow("backups", "system", "cpu_percent", false)]
    [DataRow("", "system", "cpu_percent", true)]
    [DataRow("", "agent", "agent_rss_bytes", true)]
    [DataRow("", "mariadb", "mariadb_threads_connected", true)]
    [DataRow("future", "system", "cpu_percent", true)]
    [DataRow("future", "agent", "agent_rss_bytes", true)]
    public void ConcernsMapsCollectorToLines(string collector, string source, string name, bool expected)
    {
        // Arrange
        var gap = new SeriesGap
                  {
                      Collector = collector
                  };

        // Act
        var concerns = gap.Concerns(source, name);

        // Assert
        Assert.AreEqual(expected, concerns, $"collector '{collector}' against the line {source}/{name}");
    }

    #endregion // Methods
}