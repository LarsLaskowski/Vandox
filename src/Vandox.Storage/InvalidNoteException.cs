namespace Vandox.Storage;

/// <summary>
/// Reports a note the store refuses.
/// </summary>
public sealed class InvalidNoteException : StoreException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidNoteException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public InvalidNoteException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidNoteException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public InvalidNoteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}