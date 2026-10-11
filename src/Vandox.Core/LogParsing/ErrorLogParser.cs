#pragma warning disable RH2003, S2325

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
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region ILogParser

    /// <inheritdoc />
    public string Type => throw new NotImplementedException();

    /// <inheritdoc />
    public bool LineFramed => true;

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // ILogParser
}