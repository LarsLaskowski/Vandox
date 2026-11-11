namespace Vandox.Backend.Tests;

/// <summary>
/// A string writer that cancels a token as soon as anything is written to it, through the string or the span overload (the asynchronous string overloads of .NET 11 write through the span overload).
/// </summary>
internal sealed class CancellingWriter : StringWriter
{
    #region Fields

    private readonly CancellationTokenSource _source;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CancellingWriter"/> class.
    /// </summary>
    /// <param name="source">The source that is cancelled by the first write</param>
    internal CancellingWriter(CancellationTokenSource source)
    {
        _source = source;
    }

    #endregion // Constructors

    #region TextWriter

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer)
    {
        base.Write(buffer);
        _source.Cancel();
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        base.Write(value);
        _source.Cancel();
    }

    #endregion // TextWriter
}