namespace Vandox.Core.Wire;

/// <summary>
/// Classifies a <see cref="WireException"/>.
/// </summary>
public enum WireErrorKind
{
    /// <summary>
    /// The major version of the format is not supported or missing.
    /// </summary>
    UnsupportedVersion,

    /// <summary>
    /// The stream is not a well-formed batch.
    /// </summary>
    Malformed,

    /// <summary>
    /// A record names a kind that does not exist.
    /// </summary>
    UnknownKind,

    /// <summary>
    /// The sequence numbers do not strictly increase.
    /// </summary>
    Sequence,

    /// <summary>
    /// The batch has no records.
    /// </summary>
    EmptyBatch,

    /// <summary>
    /// A limit of the decoder is exceeded.
    /// </summary>
    LimitExceeded,

    /// <summary>
    /// The header or a record breaks a rule of the model.
    /// </summary>
    Invalid
}