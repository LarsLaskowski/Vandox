using System.Text;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="PatternStream"/>
/// </summary>
[TestClass]
public class PatternStreamTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The stream delivers the header, the pattern repeated up to its length, and the trailer, whatever the read size.
    /// </summary>
    /// <param name="patternLength">The number of bytes of repeated pattern</param>
    /// <param name="readSize">The size of the reads</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(0, 5)]
    [DataRow(7, 3)]
    [DataRow(20000, 4096)]
    [DataRow(20000, 7)]
    public async Task PatternStreamDeliversHeaderPatternAndTrailer(int patternLength, int readSize)
    {
        // Arrange
        using var stream = new PatternStream("HEAD"u8.ToArray(), "abc"u8.ToArray(), patternLength, "TAIL"u8.ToArray());
        var buffer = new byte[readSize];
        var result = new StringBuilder();

        // Act
        for (var read = await stream.ReadAsync(buffer, TestContext.CancellationToken); read > 0; read = await stream.ReadAsync(buffer, TestContext.CancellationToken))
        {
            result.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        // Assert
        var repeated = string.Concat(Enumerable.Range(0, patternLength).Select(index => "abc"[index % 3]));
        var expected = $"HEAD{repeated}TAIL";

        Assert.AreEqual(expected, result.ToString(), "content");
        Assert.AreEqual(expected.Length, stream.Length, "length");
    }

    #endregion // Methods
}