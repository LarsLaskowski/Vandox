using Vandox.Core.Model;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

/// <summary>
/// Serializes a payload to UTF-8 JSON in a bounded buffer.
/// </summary>
internal static class PayloadJson
{
    #region Methods

    /// <summary>
    /// Serializes a payload exactly as <see cref="PayloadRegistry.Options"/> does.
    /// </summary>
    /// <param name="payload">The payload</param>
    /// <param name="maxBytes">The most UTF-8 bytes of the JSON</param>
    /// <returns>The JSON, or <c>null</c> when it is longer than <paramref name="maxBytes"/></returns>
    internal static byte[]? Serialize(IPayload payload, int maxBytes)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks whether the JSON of a payload is not longer than a limit, without keeping it.
    /// </summary>
    /// <param name="payload">The payload</param>
    /// <param name="maxBytes">The most UTF-8 bytes of the JSON</param>
    /// <returns><c>true</c> when the JSON fits</returns>
    internal static bool Fits(IPayload payload, int maxBytes)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}