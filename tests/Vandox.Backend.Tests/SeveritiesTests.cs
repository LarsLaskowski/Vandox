using Vandox.Backend.Logs;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="Severities"/>
/// </summary>
[TestClass]
public class SeveritiesTests
{
    #region Methods

    /// <summary>
    /// The eight names are accepted and give the priorities 0 to 7.
    /// </summary>
    /// <param name="name">The name</param>
    /// <param name="expected">The priority</param>
    [TestMethod]
    [DataRow("emerg", (byte)0)]
    [DataRow("alert", (byte)1)]
    [DataRow("crit", (byte)2)]
    [DataRow("err", (byte)3)]
    [DataRow("warning", (byte)4)]
    [DataRow("notice", (byte)5)]
    [DataRow("info", (byte)6)]
    [DataRow("debug", (byte)7)]
    public void TryParseAcceptsTheEightNames(string name, byte expected)
    {
        // Arrange

        // Act
        var accepted = Severities.TryParse(name, out var priority);

        // Assert
        Assert.IsTrue(accepted, $"{name} is accepted");
        Assert.AreEqual(expected, priority, $"priority of {name}");
    }

    /// <summary>
    /// Every other form is refused.
    /// </summary>
    /// <param name="name">The text</param>
    [TestMethod]
    [DataRow("ERR")]
    [DataRow("Err")]
    [DataRow("3")]
    [DataRow(" err")]
    [DataRow("err ")]
    [DataRow("error")]
    [DataRow("warn")]
    [DataRow("")]
    public void TryParseRefusesOtherForms(string name)
    {
        // Arrange

        // Act
        var accepted = Severities.TryParse(name, out _);

        // Assert
        Assert.IsFalse(accepted, $"'{name}' is refused");
    }

    /// <summary>
    /// Every priority from 0 to 7 has its name.
    /// </summary>
    [TestMethod]
    public void NameNamesEveryPriority()
    {
        // Arrange
        var expected = new[] { "emerg", "alert", "crit", "err", "warning", "notice", "info", "debug" };

        // Act
        var names = Enumerable.Range(0, 8).Select(priority => Severities.Name((byte)priority)).ToArray();

        // Assert
        Assert.AreSequenceEqual(expected, names, "names of the priorities 0 to 7");
    }

    #endregion // Methods
}