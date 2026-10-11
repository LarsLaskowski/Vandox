using NodaTime;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the Plesk panel log and the nginx and Apache error logs.
/// </summary>
public sealed class ErrorLogParser : ILogParser
{
    #region Constants

    /// <summary>
    /// The source type of the Plesk panel log.
    /// </summary>
    public const string PleskPanelType = "plesk-panel";

    /// <summary>
    /// The source type of the sw-cp-server error log.
    /// </summary>
    public const string SwCpServerType = "sw-cp-server-error";

    /// <summary>
    /// The source type of the nginx error log.
    /// </summary>
    public const string NginxType = "nginx-error";

    /// <summary>
    /// The source type of the Apache error log.
    /// </summary>
    public const string ApacheType = "apache-error";

    #endregion // Constants

    #region Fields

    private static readonly byte[] _bom = [0xEF, 0xBB, 0xBF];

    private readonly ErrorLogFormat _format;
    private readonly string _path;
    private readonly bool _claimsContent;
    private readonly DateTimeZone? _timeZone;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorLogParser"/> class.
    /// </summary>
    /// <param name="type">The source type</param>
    /// <param name="format">The line format</param>
    /// <param name="path">The configured path of the log</param>
    /// <param name="claimsContent">Whether the parser recognizes the format by content</param>
    /// <param name="timeZone">The zone of the local times; <c>null</c> when not set</param>
    public ErrorLogParser(string type, ErrorLogFormat format, string path, bool claimsContent, DateTimeZone? timeZone)
    {
        Type = type;
        _format = format;
        _path = path;
        _claimsContent = claimsContent;
        _timeZone = timeZone;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Tells whether the first line of a head has the format of the parser.
    /// </summary>
    /// <param name="head">The first bytes of the file</param>
    /// <returns><c>true</c> when the first line is of the format</returns>
    private bool HasFormatLine(ReadOnlySpan<byte> head)
    {
        var first = head;

        if (first.StartsWith(_bom))
        {
            first = first[_bom.Length..];
        }

        var lineEnd = first.IndexOf((byte)'\n');

        if (lineEnd >= 0)
        {
            first = first[..lineEnd];
        }

        if (first.Length > 0 && first[^1] == (byte)'\r')
        {
            first = first[..^1];
        }

        return ErrorLogLine.TryParse(_format, Utf8Text.Decode(first, int.MaxValue, out _)) is not null;
    }

    #endregion // Methods

    #region ILogParser

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public bool LineFramed => true;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        if (LogPath.Matches(_path, file.Name))
        {
            return Confidence.MatchPath;
        }

        if (_claimsContent && SyslogParser.HasSyslogName(file.Name))
        {
            return Confidence.NoMatch;
        }

        return _claimsContent && HasFormatLine(head) ? Confidence.MatchContent : Confidence.NoMatch;
    }

    /// <inheritdoc />
    public async Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        var reader = new LogLineReader(input);
        var session = new ErrorLogParseSession(file, Type, _format, _timeZone, output);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = session.Map(reader);

            if (record is not null)
            {
                await output.RecordAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    #endregion // ILogParser
}