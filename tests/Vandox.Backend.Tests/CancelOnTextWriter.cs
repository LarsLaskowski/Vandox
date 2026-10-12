namespace Vandox.Backend.Tests;

/// <summary>
/// A string writer that cancels a token as soon as the text written so far contains a marker.
/// </summary>
internal sealed class CancelOnTextWriter : StringWriter
{
    #region Fields

    private readonly CancellationTokenSource _source;

    private readonly string _marker;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CancelOnTextWriter"/> class.
    /// </summary>
    /// <param name="source">The source that is cancelled</param>
    /// <param name="marker">The text that cancels the source once it was written</param>
    internal CancelOnTextWriter(CancellationTokenSource source, string marker)
    {
        _source = source;
        _marker = marker;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Cancels the source when the marker was written.
    /// </summary>
    private void CancelWhenMarked()
    {
        if (ToString().Contains(_marker, StringComparison.Ordinal))
        {
            _source.Cancel();
        }
    }

    #endregion // Methods

    #region TextWriter

    /// <inheritdoc />
    public override void Write(string? value)
    {
        base.Write(value);
        CancelWhenMarked();
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        base.WriteLine(value);
        CancelWhenMarked();
    }

    #endregion // TextWriter
}