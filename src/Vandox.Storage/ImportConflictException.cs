namespace Vandox.Storage;

/// <summary>
/// Reports a batch whose import step does not match the stored import state (another run advanced it, or the file is complete or unknown); nothing is written.
/// </summary>
public sealed class ImportConflictException : StoreException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportConflictException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public ImportConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportConflictException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public ImportConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}