using Vandox.Core.Wire;

namespace Vandox.Backend.Ingest;

#pragma warning disable RH2003, S2325

/// <summary>
/// The bounds of one ingest request.
/// </summary>
internal static class IngestLimits
{
    #region Constants

    /// <summary>
    /// The most bytes of the compressed request body.
    /// </summary>
    internal const int MaxBodyBytes = 2 << 20;

    /// <summary>
    /// The most decompressed bytes of a batch.
    /// </summary>
    internal const long MaxBatchBytes = 2 << 20;

    /// <summary>
    /// The longest line in bytes, without the line feed.
    /// </summary>
    internal const int MaxLineBytes = 1 << 20;

    /// <summary>
    /// The most records of a batch.
    /// </summary>
    internal const int MaxRecords = 20000;

    /// <summary>
    /// The number of requests that may hold a body buffer and write at once.
    /// </summary>
    internal const int Slots = 2;

    /// <summary>
    /// The most connections of the ingest listener.
    /// </summary>
    internal const int MaxConnections = 16;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the time one request may take, from its receive time to the end of its body.
    /// </summary>
    internal static TimeSpan RequestBudget => TimeSpan.FromSeconds(30);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the limits of the decoder for an ingest request.
    /// </summary>
    /// <returns>The limits</returns>
    internal static WireLimits Wire()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}