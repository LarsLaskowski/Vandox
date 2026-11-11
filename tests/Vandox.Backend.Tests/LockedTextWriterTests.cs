namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LockedTextWriter"/>
/// </summary>
[TestClass]
public class LockedTextWriterTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A snapshot taken while another thread writes lines holds whole lines only, and the final text holds every line once, in order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task ToStringWhileWritingReturnsWholeLines()
    {
        // Arrange
        const int lineCount = 5000;
        var writer = new LockedTextWriter();
        var newLine = writer.NewLine;
        var torn = 0;

        // Act
        var producer = Task.Run(() =>
                                {
                                    for (var index = 0; index < lineCount; index++)
                                    {
                                        writer.WriteLine($"line {index}");
                                    }
                                },
                                TestContext.CancellationToken);

        while (producer.Status < TaskStatus.RanToCompletion)
        {
            var snapshot = writer.ToString();

            torn += snapshot.Length == 0 || snapshot.EndsWith(newLine, StringComparison.Ordinal) ? 0 : 1;
        }

        await producer;

        var final = writer.ToString();

        // Assert
        Assert.AreEqual(0, torn, "every snapshot is empty or ends with a line break");

        var expected = string.Concat(Enumerable.Range(0, lineCount).Select(index => $"line {index}{newLine}"));

        Assert.AreEqual(expected, final, "the final text holds every line once, in order");
    }

    /// <summary>
    /// Text written through the different overloads appears in write order.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task WriteOverloadsAppendInOrder()
    {
        // Arrange
        var writer = new LockedTextWriter();
        var newLine = writer.NewLine;

        // Act
        WriteSynchronously(writer);
        await writer.WriteAsync("e", TestContext.CancellationToken);
        WriteLineSynchronously(writer);
        await writer.WriteLineAsync("g", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual($"abcdef{newLine}g{newLine}", writer.ToString(), "every overload appends its text in write order");
    }

    /// <summary>
    /// Clear removes the text written so far.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void ClearEmptiesText()
    {
        // Arrange
        var writer = new LockedTextWriter();

        writer.WriteLine("text");

        // Act
        writer.Clear();

        // Assert
        Assert.AreEqual(string.Empty, writer.ToString(), "the text is empty after Clear");
    }

    /// <summary>
    /// Writes the first three pieces through the synchronous overloads.
    /// </summary>
    /// <param name="writer">The writer</param>
    private static void WriteSynchronously(LockedTextWriter writer)
    {
        writer.Write('a');
        writer.Write("b");
        writer.Write("cd".AsSpan());
    }

    /// <summary>
    /// Writes the line <c>f</c> through the synchronous overload.
    /// </summary>
    /// <param name="writer">The writer</param>
    private static void WriteLineSynchronously(LockedTextWriter writer)
    {
        writer.WriteLine("f");
    }

    #endregion // Methods
}