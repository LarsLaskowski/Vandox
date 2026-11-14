namespace Vandox.Storage;

/// <summary>
/// The source and name that identify a series.
/// </summary>
public sealed class SeriesKey
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesKey"/> class.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The name</param>
    public SeriesKey(string source, string name)
    {
        Source = source;
        Name = name;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the source.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    #endregion // Properties
}