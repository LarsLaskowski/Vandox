namespace Vandox.Import;

/// <summary>
/// Remembers the first exception its inner stream threw.
/// </summary>
internal sealed class ErrorTrackingStream : FilterStream
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorTrackingStream"/> class.
    /// </summary>
    /// <param name="inner">The stream to read</param>
    internal ErrorTrackingStream(Stream inner)
        : base(inner)
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the first exception the inner stream threw; <c>null</c> when there was none.
    /// </summary>
    internal Exception? Error { get; private set; }

    #endregion // Properties

    #region FilterStream

    /// <inheritdoc />
    protected override void Failed(Exception exception)
    {
        Error ??= exception;
    }

    #endregion // FilterStream
}