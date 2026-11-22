using Microsoft.Extensions.Primitives;

using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="AddressTimes"/>
/// </summary>
[TestClass]
public class AddressTimesTests
{
    #region Methods

    /// <summary>
    /// A parameter whose value is null counts as an empty value and sets nothing, and the comparison of ranges refuses no range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void AddressTimesReadValuesTreatsANullValueAsEmpty()
    {
        // Arrange
        var times = new AddressTimes();
        var values = new Dictionary<string, StringValues>
                     {
                         ["range"] = new StringValues([null])
                     };

        // Act
        var error = times.ReadValues(values);

        // Assert
        Assert.AreEqual(string.Empty, error, "an empty value is no error");
        Assert.IsNull(times.Preset, "the preset stays unset");
    }

    /// <summary>
    /// Comparing with no range is refused.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void AddressTimesSameRangeRefusesNoRange()
    {
        // Arrange
        var times = new AddressTimes();

        // Act
        var thrown = Assert.Throws<ArgumentNullException>(() => times.SameRange(null!), "no range");

        // Assert
        Assert.AreEqual("other", thrown.ParamName, "the parameter of the refusal");
    }

    #endregion // Methods
}