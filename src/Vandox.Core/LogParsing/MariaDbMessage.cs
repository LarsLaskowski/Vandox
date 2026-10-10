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

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbMessage"/> class.
    /// </summary>
    /// <param name="first">The message of the header</param>
    /// <param name="truncated"><c>true</c> when the line reader cut the header line</param>
    internal MariaDbMessage(string first, bool truncated)
    {
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the message of the entry.
    /// </summary>
    /// <param name="truncated"><c>true</c> when any text was cut or any line omitted</param>
    /// <returns>The message</returns>
    internal string Build(out bool truncated)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}