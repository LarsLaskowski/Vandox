namespace Vandox.Storage;

/// <summary>
/// Reports an incident the store refuses.
/// </summary>
public sealed class InvalidIncidentException : StoreException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidIncidentException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public InvalidIncidentException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidIncidentException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public InvalidIncidentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}