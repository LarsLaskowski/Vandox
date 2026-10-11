using NodaTime;

namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// Reads the legacy monitoring log of top, /proc and lsof snapshots.
/// </summary>
public sealed class LegacyMonitorParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of the legacy monitoring log.
    /// </summary>
    public const string ParserType = "legacy-monitor";

    /// <summary>
    /// The message of the exception for a first header whose zone does not match the configured time zone.
    /// </summary>
    public const string ZoneMismatchFirst = "the time zone of the first snapshot header does not match import.time_zone";

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyMonitorParser"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of the snapshot headers; <c>null</c> when not set</param>
    public LegacyMonitorParser(DateTimeZone? timeZone)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region ILogParser

    /// <inheritdoc/>
    public string Type => ParserType;

    /// <inheritdoc/>
    public bool LineFramed => true;

    /// <inheritdoc/>
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // ILogParser
}