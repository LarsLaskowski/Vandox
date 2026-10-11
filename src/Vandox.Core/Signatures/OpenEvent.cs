namespace Vandox.Core.Signatures;

/// <summary>
/// An event that can still take copies. It keeps only converted fields, the host, the record IDs and counts, never a match,
/// a message or a record.
/// </summary>
internal sealed class OpenEvent
{
    #region Fields

    private readonly List<long> _recordIds = [];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Creates an event from its first line.
    /// </summary>
    /// <param name="key">The signature and the field values</param>
    /// <param name="host">The host of the line</param>
    /// <param name="capturedAt">The capture time of the line</param>
    /// <param name="recordId">The record ID of the line</param>
    internal OpenEvent(SignatureKey key, string host, DateTimeOffset capturedAt, long recordId)
    {
        Key = key;
        Host = host;
        CapturedAt = capturedAt;
        _recordIds.Add(recordId);
        LineCount = 1;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the signature and the field values.
    /// </summary>
    internal SignatureKey Key { get; }

    /// <summary>
    /// Gets the first non-empty host of the lines, or empty.
    /// </summary>
    internal string Host { get; private set; }

    /// <summary>
    /// Gets the capture time of the first line.
    /// </summary>
    internal DateTimeOffset CapturedAt { get; }

    /// <summary>
    /// Gets the number of lines.
    /// </summary>
    internal long LineCount { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Tells whether a line of a host can join the event: the hosts are equal or one of them is empty.
    /// </summary>
    /// <param name="host">The host of the line</param>
    /// <returns><c>true</c> when the line can join</returns>
    internal bool AcceptsHost(string host)
    {
        return Host.Length == 0 || host.Length == 0 || string.Equals(Host, host, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds a line to the event.
    /// </summary>
    /// <param name="host">The host of the line</param>
    /// <param name="recordId">The record ID of the line</param>
    internal void Join(string host, long recordId)
    {
        if (Host.Length == 0)
        {
            Host = host;
        }

        if (_recordIds.Count < SignatureDetector.MaxRecordIds)
        {
            _recordIds.Add(recordId);
        }

        LineCount++;
    }

    /// <summary>
    /// Creates the detected event with collections of its own.
    /// </summary>
    /// <returns>The event</returns>
    internal DetectedEvent ToDetected()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, long>(StringComparer.Ordinal);
        var fields = Key.Signature.Fields;

        for (var index = 0; index < fields.Count; index++)
        {
            var value = Key.Values[index];

            if (value.Text is not null)
            {
                texts[fields[index].Name] = value.Text;
            }
            else if (value.IsNumber)
            {
                numbers[fields[index].Name] = value.Number;
            }
        }

        return new DetectedEvent
               {
                   SignatureId = Key.Signature.Id,
                   CapturedAt = CapturedAt,
                   Host = Host,
                   Texts = texts,
                   Numbers = numbers,
                   RecordIds = Array.AsReadOnly(_recordIds.ToArray()),
                   LineCount = LineCount
               };
    }

    #endregion // Methods
}