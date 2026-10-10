using System.Text;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// The message of one entry: the header message and the kept continuation lines, bounded in size.
/// </summary>
internal sealed class MariaDbMessage
{
    #region Constants

    /// <summary>
    /// The bytes kept of the continuation lines: the text limit minus room for the marker of the omitted lines.
    /// </summary>
    internal const int KeptBytes = 16320;

    #endregion // Constants

    #region Fields

    private readonly StringBuilder _text = new();

    private bool _truncated;
    private bool _overflow;
    private long _bytes;
    private long _lines;
    private int _markChars;
    private long _markLines;
    private long _pendingEmpty;
    private long _omitted;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbMessage"/> class.
    /// </summary>
    /// <param name="first">The message of the header</param>
    /// <param name="truncated"><c>true</c> when the line reader cut the header line</param>
    internal MariaDbMessage(string first, bool truncated)
    {
        _truncated = truncated;
        _lines = 1;
        _markLines = 1;

        var kept = Utf8Text.Cut(first, KeptBytes, out _);

        _markChars = kept.Length;

        var whole = Utf8Text.Cut(first, ModelLimits.MaxTextBytes, out var cut);

        _text.Append(whole);
        _bytes = Encoding.UTF8.GetByteCount(whole);

        if (cut)
        {
            _truncated = true;
            _overflow = true;
        }
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a continuation line; the line is decoded only when it is kept.
    /// </summary>
    /// <param name="line">The raw line</param>
    /// <param name="truncated"><c>true</c> when the line reader cut the line</param>
    internal void Add(ReadOnlySpan<byte> line, bool truncated)
    {
        _truncated |= truncated;

        if (line.Length == 0)
        {
            _pendingEmpty++;

            return;
        }

        if (_overflow)
        {
            Omit();

            return;
        }

        // A decoded line takes at least as many UTF-8 bytes as the raw one, so a line that does not fit raw is not decoded.
        if (Exceeds(line.Length))
        {
            Overflow();

            return;
        }

        var text = Utf8Text.Decode(line, int.MaxValue, out _);
        var size = Encoding.UTF8.GetByteCount(text);

        if (Exceeds(size))
        {
            Overflow();

            return;
        }

        AppendEmpty(_pendingEmpty);
        _pendingEmpty = 0;
        _text.Append('\n').Append(text);
        _bytes += 1 + size;
        _lines++;
        UpdateMark();
    }

    /// <summary>
    /// Adds a continuation line that is already decoded when the message with it, and with the empty lines held back before it,
    /// stays within <see cref="ModelLimits.MaxTextBytes"/> UTF-8 bytes; an empty line is held back and always taken.
    /// </summary>
    /// <param name="line">The text of the line</param>
    /// <param name="truncated"><c>true</c> when the line was cut before</param>
    /// <returns><c>true</c> when the line was taken; <c>false</c> when it does not fit, and the message is unchanged</returns>
    internal bool TryAdd(string line, bool truncated)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the message of the entry.
    /// </summary>
    /// <param name="truncated"><c>true</c> when any text was cut or any line omitted</param>
    /// <returns>The message</returns>
    internal string Build(out bool truncated)
    {
        if (_omitted == 0)
        {
            truncated = _truncated;

            return _text.ToString();
        }

        truncated = true;

        return $"{_text.ToString(0, _markChars)}\n[{_omitted} lines omitted]";
    }

    /// <summary>
    /// Tells whether the held-back empty lines and a line of the given size do not fit: a message of at most
    /// <see cref="ModelLimits.MaxTextBytes"/> bytes is kept whole, empty lines included.
    /// </summary>
    /// <param name="size">The UTF-8 bytes of the line</param>
    /// <returns><c>true</c> when the empty lines and the line are not kept</returns>
    private bool Exceeds(int size)
    {
        return _bytes + _pendingEmpty + 1 + size > ModelLimits.MaxTextBytes;
    }

    /// <summary>
    /// Stops keeping lines because the next one does not fit: the empty lines that fit within the kept room are materialized,
    /// everything after the kept prefix is counted as omitted.
    /// </summary>
    private void Overflow()
    {
        var room = KeptBytes - _bytes;

        if (room > 0)
        {
            var count = Math.Min(_pendingEmpty, room);

            AppendEmpty(count);
            _pendingEmpty -= count;
        }

        _overflow = true;
        _omitted = _lines - _markLines;
        _lines = _markLines;
        Omit();
    }

    /// <summary>
    /// Counts the pending empty lines and the line that follows them as omitted.
    /// </summary>
    private void Omit()
    {
        _omitted += _pendingEmpty + 1;
        _pendingEmpty = 0;
    }

    /// <summary>
    /// Appends empty lines to the text; the caller guarantees that they stay within <see cref="ModelLimits.MaxTextBytes"/>.
    /// The end of the kept prefix moves over the empty lines that fit within <see cref="KeptBytes"/>, even when the whole run does not.
    /// </summary>
    /// <param name="count">The number of empty lines</param>
    private void AppendEmpty(long count)
    {
        if (count == 0)
        {
            return;
        }

        var fitting = _bytes <= KeptBytes ? Math.Min(count, KeptBytes - _bytes) : 0;

        _text.Append('\n', (int)count);
        _bytes += count;
        _lines += count;

        if (fitting > 0)
        {
            _markChars = _text.Length - (int)(count - fitting);
            _markLines = _lines - (count - fitting);
        }
    }

    /// <summary>
    /// Moves the end of the kept prefix to the end of the text when the text still fits the kept room.
    /// </summary>
    private void UpdateMark()
    {
        if (_bytes <= KeptBytes)
        {
            _markChars = _text.Length;
            _markLines = _lines;
        }
    }

    #endregion // Methods
}