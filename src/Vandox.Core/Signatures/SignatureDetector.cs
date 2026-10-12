using System.Text.RegularExpressions;

using Vandox.Core.LogParsing;
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

    private readonly SignatureCatalog _catalog;
    private readonly Queue<OpenEvent> _open = new();
    private readonly Dictionary<SignatureKey, List<OpenEvent>> _byKey = [];
    private DateTimeOffset? _previous;
    private bool _complete;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Creates a detector.
    /// </summary>
    /// <param name="catalog">The catalog to detect</param>
    public SignatureDetector(SignatureCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
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
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(closed);

        RequireNotComplete();

        if (record.Data is not LogLine line)
        {
            return;
        }

        if (_previous is not null && record.CapturedAt < _previous.Value)
        {
            throw new InvalidOperationException("signature detection: lines must be added in capture order");
        }

        _previous = record.CapturedAt;

        CloseExpired(record.CapturedAt, closed);

        foreach (var signature in _catalog.Signatures)
        {
            var match = MatchLine(signature, record.Source, line);

            if (match is not null)
            {
                Join(new SignatureKey(signature, ReadFields(signature, match)), recordId, record.CapturedAt, line.Host, closed);
            }
        }
    }

    /// <summary>
    /// Closes every open event and marks the detector complete.
    /// </summary>
    /// <param name="closed">Receives the closed events</param>
    /// <exception cref="InvalidOperationException">Called after Complete</exception>
    public void Complete(ICollection<DetectedEvent> closed)
    {
        ArgumentNullException.ThrowIfNull(closed);

        RequireNotComplete();

        _complete = true;

        while (_open.Count > 0)
        {
            CloseOldest(closed);
        }
    }

    /// <summary>
    /// Matches a line against the conditions of a signature.
    /// </summary>
    /// <param name="signature">The signature</param>
    /// <param name="source">The source of the record</param>
    /// <param name="line">The line</param>
    /// <returns>The match of the pattern, <see cref="Match.Empty"/> when there is no pattern, or <c>null</c> when a condition fails</returns>
    private static Match? MatchLine(Signature signature, string source, LogLine line)
    {
        var origin = (signature.Programs.Count == 0 && signature.Sources.Count == 0) || signature.Programs.Contains(line.Program) || signature.Sources.Contains(source);
        var eventHolds = signature.Events.Count == 0 || signature.Events.Contains(line.Event);

        if (origin && eventHolds)
        {
            return MatchMessage(signature, line.Message);
        }

        return null;
    }

    /// <summary>
    /// Matches the message condition of a signature.
    /// </summary>
    /// <param name="signature">The signature</param>
    /// <param name="message">The message</param>
    /// <returns>The match, <see cref="Match.Empty"/> when there is no pattern, or <c>null</c> when the pattern does not match</returns>
    private static Match? MatchMessage(Signature signature, string message)
    {
        if (signature.Pattern is null)
        {
            return Match.Empty;
        }

        var match = signature.Pattern.Match(message);

        return match.Success ? match : null;
    }

    /// <summary>
    /// Converts the fields of a signature from a match.
    /// </summary>
    /// <param name="signature">The signature</param>
    /// <param name="match">The match</param>
    /// <returns>The values in the order of the signature's fields</returns>
    private static FieldValue[] ReadFields(Signature signature, Match match)
    {
        var values = new FieldValue[signature.Fields.Count];

        for (var index = 0; index < values.Length; index++)
        {
            var field = signature.Fields[index];
            var group = match.Groups[field.Name];

            if (group.Success && group.Length > 0)
            {
                values[index] = Convert(field.Type, group.Value);
            }
        }

        return values;
    }

    /// <summary>
    /// Converts captured text into a field value.
    /// </summary>
    /// <param name="type">The field type</param>
    /// <param name="text">The captured text</param>
    /// <returns>The value, absent when the text does not convert</returns>
    private static FieldValue Convert(SignatureFieldType type, string text)
    {
        long number;

        switch (type)
        {
            case SignatureFieldType.Integer:
                {
                    return NumberGuard.TryInteger(text, out number) ? new FieldValue(null, number, true) : default;
                }
            case SignatureFieldType.Kibibytes:
                {
                    return NumberGuard.TryKibibytes(text, out number) ? new FieldValue(null, number, true) : default;
                }
            default:
                {
                    return new FieldValue(Utf8Text.Cut(text, MaxTextBytes, out _), 0, false);
                }
        }
    }

    /// <summary>
    /// Refuses use after completion.
    /// </summary>
    private void RequireNotComplete()
    {
        if (_complete)
        {
            throw new InvalidOperationException("signature detection: already complete");
        }
    }

    /// <summary>
    /// Closes the events whose window has passed, oldest first. The check is a subtraction, which cannot overflow.
    /// </summary>
    /// <param name="capturedAt">The capture time of the current line</param>
    /// <param name="closed">Receives the closed events</param>
    private void CloseExpired(DateTimeOffset capturedAt, ICollection<DetectedEvent> closed)
    {
        while (_open.Count > 0 && capturedAt - _open.Peek().CapturedAt > MergeWindow)
        {
            CloseOldest(closed);
        }
    }

    /// <summary>
    /// Closes the oldest open event.
    /// </summary>
    /// <param name="closed">Receives the closed event</param>
    private void CloseOldest(ICollection<DetectedEvent> closed)
    {
        var oldest = _open.Dequeue();
        var candidates = _byKey[oldest.Key];

        candidates.Remove(oldest);

        if (candidates.Count == 0)
        {
            _byKey.Remove(oldest.Key);
        }

        closed.Add(oldest.ToDetected());
    }

    /// <summary>
    /// Adds a line to the oldest compatible open event of its key, or opens an event.
    /// </summary>
    /// <param name="key">The signature and the field values</param>
    /// <param name="recordId">The record ID of the line</param>
    /// <param name="capturedAt">The capture time of the line</param>
    /// <param name="host">The host of the line</param>
    /// <param name="closed">Receives an event closed to make room</param>
    private void Join(SignatureKey key, long recordId, DateTimeOffset capturedAt, string host, ICollection<DetectedEvent> closed)
    {
        if (_byKey.TryGetValue(key, out var candidates))
        {
            var target = candidates.Find(candidate => candidate.AcceptsHost(host));

            if (target is not null)
            {
                target.Join(host, recordId);

                return;
            }
        }

        if (_open.Count >= MaxOpenEvents)
        {
            CloseOldest(closed);
        }

        var opened = new OpenEvent(key, host, capturedAt, recordId);

        _open.Enqueue(opened);

        if (_byKey.TryGetValue(key, out var existing))
        {
            existing.Add(opened);
        }
        else
        {
            _byKey[key] = [opened];
        }
    }

    #endregion // Methods
}