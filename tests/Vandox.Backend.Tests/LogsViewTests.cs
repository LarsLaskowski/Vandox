using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LogsView"/>
/// </summary>
[TestClass]
public class LogsViewTests
{
    #region Methods

    /// <summary>
    /// A refusal of the store shows the rule without the prefix of the store, and a message without the prefix is shown as it is.
    /// </summary>
    [TestMethod]
    public void RefusedCutsThePrefixOfTheStore()
    {
        // Arrange
        var prefixed = new InvalidQueryException("store: invalid query: from must be before to");
        var plain = new InvalidQueryException("some other rule");

        // Act
        var withPrefix = LogsView.Refused(prefixed);
        var withoutPrefix = LogsView.Refused(plain);

        // Assert
        Assert.AreEqual("The search cannot run: from must be before to.", withPrefix, "the rule alone");
        Assert.AreEqual("The search cannot run: some other rule.", withoutPrefix, "the message as it is");
    }

    /// <summary>
    /// A missing refusal is an argument error.
    /// </summary>
    [TestMethod]
    public void RefusedWithoutExceptionThrows()
    {
        // Arrange, Act and Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => LogsView.Refused(null!), "no refusal");
    }

    /// <summary>
    /// The options are the stored sources that an address can name, and the current source is added when it is not among them.
    /// </summary>
    [TestMethod]
    public void SourceOptionsOffersStoredSourcesAndTheCurrentOne()
    {
        // Arrange
        var bad = $"bad{(char)1}source";
        string[] stored = ["syslog", "journal", bad];

        // Act
        var none = LogsView.SourceOptions(stored, string.Empty);
        var listed = LogsView.SourceOptions(stored, "journal");
        var unknown = LogsView.SourceOptions(stored, "gone");
        var invalid = LogsView.SourceOptions(stored, bad);

        // Assert
        Assert.AreSequenceEqual(["syslog", "journal"], none.ToArray(), "a source that an address cannot name is not offered");
        Assert.AreSequenceEqual(["syslog", "journal"], listed.ToArray(), "the current source is not repeated");
        Assert.AreSequenceEqual(["syslog", "journal", "gone"], unknown.ToArray(), "the current source that is not stored is appended");
        Assert.AreSequenceEqual(["syslog", "journal", bad], invalid.ToArray(), "the current source is shown even when invalid, so the form shows what the address names");
    }

    /// <summary>
    /// A missing list of sources is an argument error.
    /// </summary>
    [TestMethod]
    public void SourceOptionsWithoutSourcesThrows()
    {
        // Arrange, Act and Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => LogsView.SourceOptions(null!, string.Empty), "no sources");
    }

    #endregion // Methods
}