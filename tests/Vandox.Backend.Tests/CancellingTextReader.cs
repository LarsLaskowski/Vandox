namespace Vandox.Backend.Tests;

/// <summary>
/// A <see cref="TextReader"/> over a text that cancels a given token source when it returns the line end <c>\n</c>, as an interruption that arrives right after the input was read.
/// </summary>
internal sealed class CancellingTextReader : TextReader
{
    #region Fields

    private readonly string _text;

    private readonly CancellationTokenSource _source;

    private int _position;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CancellingTextReader"/> class.
    /// </summary>
    /// <param name="text">The text to read</param>
    /// <param name="source">The token source to cancel when the line end is returned</param>
    internal CancellingTextReader(string text, CancellationTokenSource source)
    {
        _text = text;
        _source = source;
    }

    #endregion // Constructors

    #region TextReader

    /// <inheritdoc />
    public override int Peek()
    {
        return _position < _text.Length ? _text[_position] : -1;
    }

    /// <inheritdoc />
    public override int Read()
    {
        if (_position >= _text.Length)
        {
            return -1;
        }

        var value = _text[_position++];

        if (value == '\n')
        {
            _source.Cancel();
        }

        return value;
    }

    #endregion // TextReader
}