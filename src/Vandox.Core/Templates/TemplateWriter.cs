namespace Vandox.Core.Templates;

/// <summary>
/// Collects the characters of a template in a buffer of fixed size that belongs to the caller. Once the buffer is full, or a
/// character does not fit, nothing more is taken, so the result is always a prefix of the full template.
/// </summary>
internal ref struct TemplateWriter
{
    #region Fields

    private readonly Span<char> _buffer;
    private int _length;
    private bool _pendingSpace;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateWriter"/> struct.
    /// </summary>
    /// <param name="buffer">The buffer; its length is the most characters the template can have</param>
    internal TemplateWriter(Span<char> buffer)
    {
        _buffer = buffer;
        _length = 0;
        _pendingSpace = false;
        HasRoom = true;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether more characters can still be taken.
    /// </summary>
    internal bool HasRoom { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Records a run of separators: one space is written before the next character, unless nothing was written yet.
    /// </summary>
    internal void Separate()
    {
        _pendingSpace = _length > 0;
    }

    /// <summary>
    /// Writes one character.
    /// </summary>
    /// <param name="value">The character</param>
    internal void Append(char value)
    {
        if (Reserve() > 0)
        {
            _buffer[_length++] = value;
        }
        else
        {
            HasRoom = false;
        }
    }

    /// <summary>
    /// Writes text such as a placeholder as far as it fits; the writer is closed when part of it does not.
    /// </summary>
    /// <param name="text">The text</param>
    internal void Append(ReadOnlySpan<char> text)
    {
        var count = Math.Min(Reserve(), text.Length);

        text[..count].CopyTo(_buffer[_length..]);
        _length += count;

        if (count < text.Length)
        {
            HasRoom = false;
        }
    }

    /// <summary>
    /// Writes text of the message; a lone surrogate becomes U+FFFD.
    /// </summary>
    /// <param name="text">The text</param>
    internal void AppendText(ReadOnlySpan<char> text)
    {
        var index = 0;

        while (index < text.Length && HasRoom)
        {
            var value = text[index];

            if (char.IsHighSurrogate(value) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                Append(text.Slice(index, 2));
                index += 2;

                continue;
            }

            Append(char.IsSurrogate(value) ? '\uFFFD' : value);
            index++;
        }
    }

    /// <summary>
    /// Returns the template written so far.
    /// </summary>
    /// <returns>The template</returns>
    internal readonly string Build()
    {
        return new string(_buffer[.._length]);
    }

    /// <summary>
    /// Writes the pending space.
    /// </summary>
    /// <returns>The number of characters that still fit</returns>
    private int Reserve()
    {
        if (HasRoom)
        {
            if (_pendingSpace && _length < _buffer.Length)
            {
                _buffer[_length++] = ' ';
            }

            _pendingSpace = false;

            return _buffer.Length - _length;
        }

        return 0;
    }

    #endregion // Methods
}