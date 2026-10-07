namespace Vandox.Core.IO;

/// <summary>
/// An I/O error whose message is safe to show: it never contains a path or file content.
/// </summary>
public sealed class SafeIoException : IOException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SafeIoException"/> class.
    /// </summary>
    /// <param name="message">The message, without a path</param>
    public SafeIoException(string message)
        : base(message)
    {
    }

    #endregion // Constructors
}