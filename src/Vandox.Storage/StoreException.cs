namespace Vandox.Storage;

/// <summary>
/// Reports a request the store refuses or a failure of the database. The message names the rule that is broken and never
/// a payload value.
/// </summary>
public class StoreException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public StoreException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public StoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}