#pragma warning disable RH2003, S2325

using Vandox.Core.Model;

namespace Vandox.Core.Signatures;

/// <summary>
/// Matches stored log lines against a catalog and merges the copies of one event.
/// </summary>
public sealed class SignatureDetector
{
    #region Constants

    /// <summary>
    /// The most events open at one time.
    /// </summary>
    internal const int MaxOpenEvents = 1024;

    /// <summary>
    /// The most record IDs kept per event.
    /// </summary>
    internal const int MaxRecordIds = 256;

    /// <summary>
    /// The most UTF-8 bytes of a text field.
    /// </summary>
    internal const int MaxTextBytes = ModelLimits.MaxShortTextBytes;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The time after the first line of an event in which a copy still joins it.
    /// </summary>
    internal static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(60);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Creates a detector.
    /// </summary>
    /// <param name="catalog">The catalog to detect</param>
    public SignatureDetector(SignatureCatalog catalog)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Takes the next record and adds the events that are closed by it.
    /// </summary>
    /// <param name="recordId">The ID of the record</param>
    /// <param name="record">The record</param>
    /// <param name="closed">Receives the closed events</param>
    /// <exception cref="ArgumentNullException">record or closed is null</exception>
    /// <exception cref="InvalidOperationException">A log line before the previous one, or after Complete</exception>
    public void Add(long recordId, DataRecord record, ICollection<DetectedEvent> closed)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Closes every open event and marks the detector complete.
    /// </summary>
    /// <param name="closed">Receives the closed events</param>
    /// <exception cref="InvalidOperationException">Called after Complete</exception>
    public void Complete(ICollection<DetectedEvent> closed)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}