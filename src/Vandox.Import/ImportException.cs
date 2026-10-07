namespace Vandox.Import;

/// <summary>
/// Reports why a run ended before it finished. The message never contains a path or input text.
/// </summary>
public sealed class ImportException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public ImportException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public ImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}