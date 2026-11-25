namespace Vandox.Storage;

/// <summary>
/// A gap in the data of an agent.
/// </summary>
public sealed class SeriesGap
{
    #region Constants

    private const string AgentSource = "agent";
    private const string SystemSource = "system";
    private const string FilesystemPrefix = "filesystem_";
    private const string ClockPrefix = "clock_";

    private static readonly HashSet<string> _namedCollectors = ["self", "mariadb", "filesystem", "clock", "system", "processes", "connections", "systemd", "kernel", "logs", "backups"];

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the kind: record, sequence or time.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the cause.
    /// </summary>
    public string Cause { get; init; } = string.Empty;

    /// <summary>
    /// Gets the state.
    /// </summary>
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// Gets the collector, empty for none.
    /// </summary>
    public string Collector { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the gap.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the gap.
    /// </summary>
    public DateTimeOffset To { get; init; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns whether the gap concerns a series.
    /// </summary>
    /// <param name="source">The source of the series</param>
    /// <param name="name">The name of the series</param>
    /// <returns><see langword="true"/> when the gap concerns the series</returns>
    public bool Concerns(string source, string name)
    {
        return Collector switch
               {
                   "self" => source == AgentSource,
                   "mariadb" => source == "mariadb",
                   "filesystem" => source == SystemSource && name.StartsWith(FilesystemPrefix, StringComparison.Ordinal),
                   "clock" => source == SystemSource && name.StartsWith(ClockPrefix, StringComparison.Ordinal),
                   "system" => source == SystemSource && name.StartsWith(FilesystemPrefix, StringComparison.Ordinal) == false && name.StartsWith(ClockPrefix, StringComparison.Ordinal) == false,
                   "processes" or "connections" or "systemd" or "kernel" or "logs" or "backups" => false,
                   _ => true
               };
    }

    /// <summary>
    /// Returns whether the gap concerns a collector: it names the collector, or it names none or one the time-series query leaves unnamed.
    /// </summary>
    /// <param name="collector">The collector</param>
    /// <returns><see langword="true"/> when the gap concerns the collector</returns>
    public bool ConcernsCollector(string collector)
    {
        return Collector == collector || Collector is "" || _namedCollectors.Contains(Collector) == false;
    }

    #endregion // Methods
}