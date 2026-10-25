using Vandox.Core.Wire;

namespace Vandox.Backend.Ingest;

#pragma warning disable RH2003, S2325

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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the fixed reason text that is logged for an error of the decoder.
    /// </summary>
    /// <param name="kind">The kind of the error</param>
    /// <returns>The reason</returns>
    internal static string Reason(WireErrorKind kind)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets a value indicating whether an answer is a refusal of the batch, which the agent gives up on and which carries the refusal header.
    /// </summary>
    /// <param name="status">The HTTP status</param>
    /// <returns><c>true</c> for a refusal</returns>
    internal static bool IsRefusal(int status)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}