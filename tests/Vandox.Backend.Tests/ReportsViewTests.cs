using Vandox.Backend.Reports;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportsView"/>
/// </summary>
[TestClass]
public class ReportsViewTests
{
    #region Methods

    /// <summary>
    /// The addresses and the file name of a language are built from fixed paths, relative to the base address, and hold nothing but the language.
    /// </summary>
    /// <param name="language">The language</param>
    [TestMethod]
    [DataRow("en")]
    [DataRow("de")]
    public void ReportsViewHrefsUseOnlyFixedPaths(string language)
    {
        // Act
        var page = ReportsView.PageHref(language);
        var download = ReportsView.DownloadHref(language);
        var file = ReportsView.FileName(language);

        // Assert
        Assert.AreEqual($"report?lang={language}", page, "the page address");
        Assert.AreEqual($"report/markdown?lang={language}", download, "the download address");
        Assert.AreEqual($"vandox-report-{language}.md", file, "the file name");
    }

    #endregion // Methods
}