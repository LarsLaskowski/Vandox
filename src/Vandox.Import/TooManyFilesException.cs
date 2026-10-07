namespace Vandox.Import;

/// <summary>
/// Reports an input that holds more than <see cref="ImportLimits.MaxFiles"/> entries.
/// </summary>
public sealed class TooManyFilesException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TooManyFilesException"/> class.
    /// </summary>
    public TooManyFilesException()
        : base($"importer: too many files: more than {ImportLimits.MaxFiles} entries; split the input")
    {
    }

    #endregion // Constructors
}