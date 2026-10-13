namespace Vandox.Backend.Tests;

/// <summary>
/// A <see cref="TextReader"/> over a text that counts the characters taken from it.
/// </summary>
internal sealed class CountingTextReader : TextReader
{
    #region Fields

    private readonly string _text;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingTextReader"/> class.
    /// </summary>
    /// <param name="text">The text to read</param>
    internal CountingTextReader(string text)
    {
        _text = text;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of characters that were read so far.
    /// </summary>
    internal int Consumed { get; private set; }

    #endregion // Properties

    #region TextReader

    /// <inheritdoc />
    public override int Peek()
    {
        return Consumed < _text.Length ? _text[Consumed] : -1;
    }

    /// <inheritdoc />
    public override int Read()
    {
        return Consumed < _text.Length ? _text[Consumed++] : -1;
    }

    /// <inheritdoc />
    public override int Read(char[] buffer, int index, int count)
    {
        var length = Math.Min(count, _text.Length - Consumed);

        _text.CopyTo(Consumed, buffer, index, length);
        Consumed += length;

        return length;
    }

    #endregion // TextReader
}