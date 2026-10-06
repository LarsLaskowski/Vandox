using Vandox.Core.Model;

namespace Vandox.Core.Wire;

/// <summary>
/// Locates a decoding error in the stream. The message never contains payload text.
/// </summary>
public sealed class WireException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WireException"/> class.
    /// </summary>
    /// <param name="kind">The class of the error</param>
    /// <param name="line">The line of the stream, counted from 1</param>
    /// <param name="reason">What is wrong, without payload text</param>
    /// <param name="fieldError">The rule of the model that is broken, if any</param>
    public WireException(WireErrorKind kind, int line, string reason, FieldError? fieldError = null)
        : base($"wire: line {line}: {(fieldError is null ? reason : fieldError.Message)}")
    {
        Kind = kind;
        Line = line;
        Reason = reason;
        FieldError = fieldError;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the class of the error.
    /// </summary>
    public WireErrorKind Kind { get; }

    /// <summary>
    /// Gets the line of the stream, counted from 1.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// Gets what is wrong.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Gets the rule of the model that is broken, if any.
    /// </summary>
    public FieldError? FieldError { get; }

    #endregion // Properties
}