namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="BoundedBufferWriter"/>
/// </summary>
[TestClass]
public class BoundedBufferWriterTests
{
    #region Methods

    /// <summary>
    /// Bytes advanced up to the limit are kept, also across several buffers.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterKeepsBytesUpToTheLimit()
    {
        // Arrange
        var writer = new BoundedBufferWriter(600);

        // Act
        for (var round = 0; round < 3; round++)
        {
            var span = writer.GetSpan(200);

            span[..200].Fill((byte)(round + 1));
            writer.Advance(200);
        }

        // Assert
        Assert.IsFalse(writer.Exceeded, "exactly the limit is not exceeded");
        Assert.AreEqual(600, writer.WrittenSpan.Length, "written length");
        Assert.AreEqual((byte)1, writer.WrittenSpan[0], "first round survives growing");
        Assert.AreEqual((byte)2, writer.WrittenSpan[200], "second round");
        Assert.AreEqual((byte)3, writer.WrittenSpan[599], "third round");
    }

    /// <summary>
    /// A negative limit is refused.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterRefusesANegativeLimit()
    {
        // Act and assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedBufferWriter(-1), "negative limit");
    }

    /// <summary>
    /// A negative count is refused.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterAdvanceRefusesANegativeCount()
    {
        // Arrange
        var writer = new BoundedBufferWriter(10);

        // Act and assert
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1), "negative count");
    }

    /// <summary>
    /// A negative size hint is refused by both accessors.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterRefusesANegativeSizeHint()
    {
        // Arrange
        var writer = new BoundedBufferWriter(10);

        // Act and assert
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetSpan(-1), "span");
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetMemory(-1), "memory");
    }

    /// <summary>
    /// Advancing before any buffer was handed out, or beyond the room handed out, is refused.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterAdvancePastTheEndThrows()
    {
        // Arrange
        var withoutBuffer = new BoundedBufferWriter(10);
        var withBuffer = new BoundedBufferWriter(10);
        var room = withBuffer.GetMemory(0).Length;

        // Act and assert
        Assert.Throws<InvalidOperationException>(() => withoutBuffer.Advance(1), "no buffer handed out");
        Assert.Throws<InvalidOperationException>(() => withBuffer.Advance(room + 1), "more than the room handed out");
        Assert.IsFalse(withBuffer.Exceeded, "a refused advance does not exceed the limit");
    }

    /// <summary>
    /// Advancing zero bytes without a buffer is harmless.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterAdvanceZeroWithoutBufferIsAccepted()
    {
        // Arrange
        var writer = new BoundedBufferWriter(10);

        // Act
        writer.Advance(0);

        // Assert
        Assert.IsFalse(writer.Exceeded, "not exceeded");
        Assert.AreEqual(0, writer.WrittenSpan.Length, "nothing written");
    }

    /// <summary>
    /// A size hint that no array can hold fails with a message that names the cause.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterRefusesASizeHintNoArrayCanHold()
    {
        // Arrange
        var writer = new BoundedBufferWriter(10);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => writer.GetSpan(int.MaxValue), "hint above the array limit");

        // Assert
        Assert.AreEqual("The buffer cannot grow any further.", exception.Message, "message");
    }

    /// <summary>
    /// More bytes than the limit drop what was written, and the writer keeps accepting bytes without keeping them.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterDropsTheBytesOnceExceeded()
    {
        // Arrange
        var writer = new BoundedBufferWriter(4);

        writer.GetSpan(10)[..5].Fill(7);

        // Act
        writer.Advance(5);

        var scratch = writer.GetSpan(1000);

        scratch.Fill(9);
        writer.Advance(1000);

        // Assert
        Assert.IsTrue(writer.Exceeded, "limit exceeded");
        Assert.AreEqual(0, writer.WrittenSpan.Length, "written bytes are dropped");
        Assert.IsGreaterThanOrEqualTo(1000, scratch.Length, "scratch room covers the hint");
    }

    /// <summary>
    /// Exceeded is sticky: later advances, also of zero bytes, neither re-arm the writer nor bring bytes back.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterExceededStaysExceeded()
    {
        // Arrange
        var writer = new BoundedBufferWriter(2);

        writer.GetSpan(0)[..3].Fill(1);
        writer.Advance(3);

        // Act
        writer.Advance(0);
        writer.GetSpan(0)[..1].Fill(2);
        writer.Advance(1);

        // Assert
        Assert.IsTrue(writer.Exceeded, "still exceeded");
        Assert.AreEqual(0, writer.WrittenSpan.Length, "no bytes come back");
    }

    /// <summary>
    /// Once exceeded, a large number of bytes can be advanced even though nothing was handed out for them, and the scratch array is reused.
    /// </summary>
    [TestMethod]
    public void BoundedBufferWriterReusesTheScratchArrayWhenExceeded()
    {
        // Arrange
        var writer = new BoundedBufferWriter(0);

        writer.GetSpan(0)[..1].Fill(1);
        writer.Advance(1);

        // Act
        var first = writer.GetMemory(300);
        var second = writer.GetMemory(100);
        var larger = writer.GetMemory(5000);

        // Assert
        Assert.IsTrue(writer.Exceeded, "exceeded");
        Assert.IsTrue(first.Equals(second), "the same scratch array is handed out for a smaller hint");
        Assert.IsGreaterThanOrEqualTo(5000, larger.Length, "a larger hint gets a larger scratch array");
    }

    #endregion // Methods
}