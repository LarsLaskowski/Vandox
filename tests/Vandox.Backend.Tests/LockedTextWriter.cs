using System.Text;

namespace Vandox.Backend.Tests;

/// <summary>
/// A text writer that keeps what is written in memory and lets another thread read it while the writes go on: every write, <see cref="ToString"/> and <see cref="Clear"/> take the same lock.
/// </summary>
internal sealed class LockedTextWriter : TextWriter
{
    #region Fields

    private readonly Lock _lock = new();
    private readonly StringBuilder _text = new();

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Removes everything written so far.
    /// </summary>
    internal void Clear()
    {
        lock (_lock)
        {
            _text.Clear();
        }
    }

    #endregion // Methods

    #region TextWriter

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.Unicode;

    /// <inheritdoc />
    public override void Write(char value)
    {
        lock (_lock)
        {
            _text.Append(value);
        }
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count)
    {
        lock (_lock)
        {
            _text.Append(buffer, index, count);
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        lock (_lock)
        {
            _text.Append(value);
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            _text.Append(value).Append(CoreNewLine);
        }
    }

    #endregion // TextWriter

    #region Object

    /// <inheritdoc />
    public override string ToString()
    {
        lock (_lock)
        {
            return _text.ToString();
        }
    }

    #endregion // Object
}