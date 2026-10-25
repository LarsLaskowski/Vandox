using Microsoft.AspNetCore.Http;

using Vandox.Core.Wire;

namespace Vandox.Backend.Ingest;

/// <summary>
/// Maps the errors of the decoder to the answers of the ingest endpoint.
/// </summary>
internal static class IngestStatus
{
    #region Methods

    /// <summary>
    /// Gets the HTTP status for an error of the decoder.
    /// </summary>
    /// <param name="kind">The kind of the error</param>
    /// <returns>The status</returns>
    internal static int ForWireError(WireErrorKind kind)
    {
        return kind switch
               {
                   WireErrorKind.Malformed => StatusCodes.Status400BadRequest,
                   WireErrorKind.LimitExceeded => StatusCodes.Status413PayloadTooLarge,
                   WireErrorKind.UnsupportedVersion => StatusCodes.Status415UnsupportedMediaType,
                   _ => StatusCodes.Status422UnprocessableEntity
               };
    }

    /// <summary>
    /// Gets the fixed reason text that is logged for an error of the decoder.
    /// </summary>
    /// <param name="kind">The kind of the error</param>
    /// <returns>The reason</returns>
    internal static string Reason(WireErrorKind kind)
    {
        return kind switch
               {
                   WireErrorKind.UnsupportedVersion => "format version",
                   WireErrorKind.Malformed => "malformed",
                   WireErrorKind.UnknownKind => "unknown kind",
                   WireErrorKind.Sequence => "sequence",
                   WireErrorKind.EmptyBatch => "empty batch",
                   WireErrorKind.LimitExceeded => "limit exceeded",
                   _ => "invalid"
               };
    }

    /// <summary>
    /// Gets a value indicating whether an answer is a refusal of the batch, which the agent gives up on and which carries the refusal header.
    /// </summary>
    /// <param name="status">The HTTP status</param>
    /// <returns><c>true</c> for a refusal</returns>
    internal static bool IsRefusal(int status)
    {
        return status is StatusCodes.Status400BadRequest or StatusCodes.Status413PayloadTooLarge or StatusCodes.Status422UnprocessableEntity;
    }

    #endregion // Methods
}