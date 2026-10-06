namespace Vandox.Import;

/// <summary>
/// Reports a content that is not the one that was hashed in pass 1.
/// </summary>
public sealed class ContentChangedException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentChangedException"/> class.
    /// </summary>
    public ContentChangedException()
        : base("the content changed while it was imported")
    {
    }

    #endregion // Constructors
}