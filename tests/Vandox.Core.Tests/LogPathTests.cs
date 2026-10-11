using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LogPath"/>
/// </summary>
[TestClass]
public class LogPathTests
{
    #region Methods

    /// <summary>
    /// A file matches when its parent directory and base name are those of the configured path, alone or with a rotation suffix of a dot and digits or a dash and eight digits; the comparison is ordinal and case-sensitive, and the root of a copy does not matter.
    /// </summary>
    /// <param name="configuredPath">The configured path</param>
    /// <param name="fileName">The display path of the file</param>
    /// <param name="expected">Whether the file matches</param>
    [TestMethod]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log", true)]
    [DataRow("/var/log/nginx/error.log", "var/log/nginx/error.log", true)]
    [DataRow("/var/log/nginx/error.log", "server-2026-10/nginx/error.log", true)]
    [DataRow("/var/log/nginx/error.log", "a/b/c/nginx/error.log", true)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.1", true)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.12", true)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.0", true)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log-20261010", true)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.1a", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.a1", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.1.2", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log-2026101", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log-202610101", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log-2026101a", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log-", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log.old", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.logx", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/error.log1", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/xerror.log", false)]
    [DataRow("/var/log/nginx/error.log", "error.log", false)]
    [DataRow("/var/log/nginx/error.log", "apache2/error.log", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/sub/error.log", false)]
    [DataRow("/var/log/nginx/error.log", "Nginx/error.log", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/Error.log", false)]
    [DataRow("/var/log/nginx/error.log", "nginx", false)]
    [DataRow("/var/log/nginx/error.log", "", false)]
    [DataRow("/var/log/nginx/error.log", "nginx/", false)]
    [DataRow("/usr/local/psa/admin/logs/panel.log", "logs/panel.log", true)]
    [DataRow("/usr/local/psa/admin/logs/panel.log", "psa/admin/logs/panel.log.3", true)]
    [DataRow("/usr/local/psa/admin/logs/panel.log", "admin/panel.log", false)]
    [DataRow("/var/log/plesk/panel.log", "logs/panel.log", false)]
    [DataRow("/var/log/sw-cp-server/error_log", "sw-cp-server/error_log", true)]
    [DataRow("/var/log/sw-cp-server/error_log", "copy/sw-cp-server/error_log-20261010", true)]
    [DataRow("/error.log", "error.log", false)]
    [DataRow("/error.log", "nginx/error.log", false)]
    [DataRow("/error.log", "/error.log", false)]
    [DataRow("/", "nginx/error.log", false)]
    [DataRow("/", "", false)]
    [DataRow("/", "/", false)]
    [DataRow("", "nginx/error.log", false)]
    [DataRow("error.log", "error.log", false)]
    public void LogPathMatchesParentDirectoryAndBaseNameWithRotationSuffix(string configuredPath, string fileName, bool expected)
    {
        // Act
        var matches = LogPath.Matches(configuredPath, fileName);

        // Assert
        Assert.AreEqual(expected, matches, $"\"{fileName}\" against \"{configuredPath}\"");
    }

    /// <summary>
    /// A digit of another script is no rotation digit: only ASCII digits count.
    /// </summary>
    /// <param name="suffix">The suffix after the base name, with a placeholder for one non-ASCII digit</param>
    [TestMethod]
    [DataRow(".#")]
    [DataRow(".1#")]
    [DataRow("-2026101#")]
    [DataRow("-########")]
    public void LogPathDoesNotTakeNonAsciiDigitsAsRotationDigits(string suffix)
    {
        // Arrange
        var arabicIndicDigitOne = ((char)0x0661).ToString();
        var fileName = "nginx/error.log" + suffix.Replace("#", arabicIndicDigitOne, StringComparison.Ordinal);

        // Act
        var matches = LogPath.Matches("/var/log/nginx/error.log", fileName);

        // Assert
        Assert.IsFalse(matches, "a digit that is not an ASCII digit is no rotation digit");
        Assert.Contains(arabicIndicDigitOne, fileName, "the file name holds the non-ASCII digit");
    }

    #endregion // Methods
}