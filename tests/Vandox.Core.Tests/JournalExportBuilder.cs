using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Vandox.Core.Tests;

/// <summary>
/// Builds the bytes of a journal export (<c>journalctl -o export</c>) from text and binary fields.
/// </summary>
internal sealed class JournalExportBuilder
{
    #region Fields

    private readonly List<byte> _bytes = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of bytes built so far.
    /// </summary>
    internal int Length => _bytes.Count;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a text field, <c>NAME=value</c> and a line feed.
    /// </summary>
    /// <param name="name">The field name</param>
    /// <param name="value">The value, written as UTF-8</param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder Text(string name, string value)
    {
        return Text(name, Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Adds a text field whose value is given as raw bytes.
    /// </summary>
    /// <param name="name">The field name</param>
    /// <param name="value">The value bytes</param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder Text(string name, byte[] value)
    {
        _bytes.AddRange(Encoding.UTF8.GetBytes(name + "="));
        _bytes.AddRange(value);
        _bytes.Add((byte)'\n');

        return this;
    }

    /// <summary>
    /// Adds a binary field: the name, a line feed, the length as 64-bit little-endian number, the bytes and a line feed.
    /// </summary>
    /// <param name="name">The field name</param>
    /// <param name="value">The value bytes</param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder Binary(string name, byte[] value)
    {
        return Binary(name, (ulong)value.Length, value);
    }

    /// <summary>
    /// Adds a binary field that declares a length which may differ from the bytes that follow.
    /// </summary>
    /// <param name="name">The field name</param>
    /// <param name="declared">The declared length</param>
    /// <param name="value">The bytes that follow the length</param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder Binary(string name, ulong declared, byte[] value)
    {
        var length = new byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64LittleEndian(length, declared);
        _bytes.AddRange(Encoding.UTF8.GetBytes(name));
        _bytes.Add((byte)'\n');
        _bytes.AddRange(length);
        _bytes.AddRange(value);
        _bytes.Add((byte)'\n');

        return this;
    }

    /// <summary>
    /// Adds the start of a binary field without its bytes: the name, a line feed and the declared length.
    /// </summary>
    /// <param name="name">The field name</param>
    /// <param name="declared">The declared length</param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder BinaryHeader(string name, ulong declared)
    {
        var length = new byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64LittleEndian(length, declared);
        _bytes.AddRange(Encoding.UTF8.GetBytes(name));
        _bytes.Add((byte)'\n');
        _bytes.AddRange(length);

        return this;
    }

    /// <summary>
    /// Adds bytes as they are.
    /// </summary>
    /// <param name="value">The bytes</param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder Raw(byte[] value)
    {
        _bytes.AddRange(value);

        return this;
    }

    /// <summary>
    /// Adds text as it is, written as UTF-8.
    /// </summary>
    /// <param name="value">The text</param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder Raw(string value)
    {
        return Raw(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Ends the entry with an empty line.
    /// </summary>
    /// <returns>The builder</returns>
    internal JournalExportBuilder End()
    {
        _bytes.Add((byte)'\n');

        return this;
    }

    /// <summary>
    /// Adds a complete entry with the fields the parsers keep, and the empty line that ends it.
    /// </summary>
    /// <param name="microseconds">The <c>__REALTIME_TIMESTAMP</c> as text</param>
    /// <param name="host">The <c>_HOSTNAME</c></param>
    /// <param name="identifier">The <c>SYSLOG_IDENTIFIER</c></param>
    /// <param name="pid">The <c>_PID</c></param>
    /// <param name="priority">The <c>PRIORITY</c>, or <c>null</c> to leave it out</param>
    /// <param name="message">The <c>MESSAGE</c></param>
    /// <returns>The builder</returns>
    internal JournalExportBuilder Entry(string microseconds, string host, string identifier, int pid, int? priority, string message)
    {
        Text("__CURSOR", "s=0123456789abcdef0123456789abcdef;i=1;b=0b6f9b0c2d1e4c439a4e7f1b2c3d4e5f;m=1;t=1;x=1");
        Text("__REALTIME_TIMESTAMP", microseconds);
        Text("__MONOTONIC_TIMESTAMP", "123456789");

        if (priority is { } value)
        {
            Text("PRIORITY", value.ToString(CultureInfo.InvariantCulture));
        }

        Text("SYSLOG_IDENTIFIER", identifier);
        Text("_PID", pid.ToString(CultureInfo.InvariantCulture));
        Text("_HOSTNAME", host);
        Text("MESSAGE", message);

        return End();
    }

    /// <summary>
    /// Returns the bytes built so far.
    /// </summary>
    /// <returns>The bytes</returns>
    internal byte[] ToArray()
    {
        return [.. _bytes];
    }

    #endregion // Methods
}