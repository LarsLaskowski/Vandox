using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Core.Reports;

namespace Vandox.Storage;

/// <summary>
/// Holds the SQL texts of the listener mark writer and of the two listener reads, validates the query and maps the rows.
/// </summary>
internal static class ListenerQueries
{
    #region Constants

    /// <summary>
    /// The listener check of a snapshot whose new listeners the agent compared on all ports.
    /// </summary>
    internal const string CheckAll = "all";

    /// <summary>
    /// The listener check of a snapshot whose new listeners the agent compared on ports below 1024.
    /// </summary>
    internal const string CheckPrivileged = "privileged";

    /// <summary>
    /// The listener check of the first snapshot after an agent start.
    /// </summary>
    internal const string CheckStart = "start";

    private const string NoEarlierText = "no_earlier";

    private const string NotComparableText = "not_comparable";

    private const string PrivilegedIncompleteText = "privileged_incomplete";

    private const string AllPortsText = "all_ports";

    private const string PrivilegedOnlyText = "privileged_only";

    private const string Context = "store: reading listener findings";

    private const string AgentList = "SELECT agent_id, count(*), min(captured_at), max(captured_at) FROM connection_marks GROUP BY agent_id ORDER BY agent_id LIMIT $limit";

    private const string AgentCount = "SELECT count(DISTINCT agent_id) FROM connection_marks";

    private const string StartCounts = "SELECT agent_id, outcome, count(*) FROM listener_comparisons WHERE agent_id <= $last GROUP BY agent_id, outcome";

    private const string StartFindings = "SELECT agent_id, seq, captured_at, base_captured_at, outcome, new_count, new_listeners FROM listener_comparisons WHERE finding = 1 "
                                         + "ORDER BY captured_at DESC, agent_id, seq DESC LIMIT $limit";

    private const string StartTotal = "SELECT count(*) FROM listener_comparisons WHERE finding = 1";

    private const string AgentFindings = "SELECT agent_id, seq, captured_at, NULL, listener_check, new_count, new_listeners FROM connection_marks WHERE new_count > 0 "
                                         + "ORDER BY captured_at DESC, agent_id, seq DESC LIMIT $limit";

    private const string AgentTotal = "SELECT count(*) FROM connection_marks WHERE new_count > 0";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Validates a finding query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void Validate(ListenerFindingQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(Enum.IsDefined(query.Kind), "kind must be a known finding kind");
        RecordQueries.Require(query.Limit is >= 1 and <= ListenerReport.MaxFindings, $"limit must be between 1 and {ListenerReport.MaxFindings}");
    }

    /// <summary>
    /// Writes the text a listener list is stored as.
    /// </summary>
    /// <param name="listeners">The listeners</param>
    /// <returns>The JSON array</returns>
    internal static string SerializeListeners(IEnumerable<Listener> listeners)
    {
        return JsonSerializer.Serialize(listeners.ToList(), PayloadRegistry.Options);
    }

    /// <summary>
    /// Returns the text an outcome is stored as.
    /// </summary>
    /// <param name="outcome">The outcome</param>
    /// <returns>The text</returns>
    internal static string OutcomeText(ListenerOutcome outcome)
    {
        return outcome switch
               {
                   ListenerOutcome.NoEarlierInventory => NoEarlierText,
                   ListenerOutcome.PrivilegedIncomplete => PrivilegedIncompleteText,
                   ListenerOutcome.AllPorts => AllPortsText,
                   ListenerOutcome.PrivilegedOnly => PrivilegedOnlyText,
                   _ => NotComparableText
               };
    }

    /// <summary>
    /// Determines whether an outcome says that the start was not compared.
    /// </summary>
    /// <param name="outcome">The outcome</param>
    /// <returns><c>true</c> when the start was not compared</returns>
    internal static bool IsNotCompared(ListenerOutcome outcome)
    {
        return outcome is not (ListenerOutcome.AllPorts or ListenerOutcome.PrivilegedOnly);
    }

    /// <summary>
    /// Reads the counts of the lowest agent IDs.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the counts</returns>
    internal static async Task<ListenerTallyPage> ReadTalliesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var limit = new Dictionary<string, object>
                    {
                        ["$limit"] = (long)ListenerReport.MaxAgents
                    };
        var agents = await ReaderQuery.ReadAsync(connection, AgentList, limit, ReadAgent, Context, cancellationToken).ConfigureAwait(false);

        if (agents.Count == 0)
        {
            return new ListenerTallyPage();
        }

        var last = new Dictionary<string, object>
                   {
                       ["$last"] = agents[^1].AgentId
                   };
        var starts = await ReaderQuery.ReadAsync(connection, StartCounts, last, ReadStartCount, Context, cancellationToken).ConfigureAwait(false);
        var omitted = 0L;

        if (agents.Count == ListenerReport.MaxAgents)
        {
            var total = await ReaderQuery.ReadAsync(connection, AgentCount, new Dictionary<string, object>(), reader => reader.GetInt64(0), Context, cancellationToken).ConfigureAwait(false);

            omitted = total[0] - agents.Count;
        }

        return new ListenerTallyPage
               {
                   Agents = [.. agents.Select(agent => WithStarts(agent, starts))],
                   Omitted = omitted
               };
    }

    /// <summary>
    /// Reads the latest findings of one kind and their total.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the page</returns>
    internal static async Task<ListenerFindingPage> ReadFindingsAsync(SqliteConnection connection, ListenerFindingQuery query, CancellationToken cancellationToken)
    {
        var agent = query.Kind == ListenerFindingKind.Agent;
        var limit = new Dictionary<string, object>
                    {
                        ["$limit"] = (long)query.Limit
                    };
        var total = await ReaderQuery.ReadAsync(connection, agent ? AgentTotal : StartTotal, new Dictionary<string, object>(), reader => reader.GetInt64(0), Context, cancellationToken).ConfigureAwait(false);
        var findings = await ReaderQuery.ReadAsync(connection, agent ? AgentFindings : StartFindings, limit, row => ReadFinding(row, query.Kind), Context, cancellationToken).ConfigureAwait(false);

        return new ListenerFindingPage
               {
                   Findings = findings,
                   Total = total[0]
               };
    }

    /// <summary>
    /// Maps a row of the agent list to a tally without start counts.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The tally</returns>
    private static ListenerTally ReadAgent(SqliteDataReader reader)
    {
        return new ListenerTally
               {
                   AgentId = reader.GetString(0),
                   Snapshots = reader.GetInt64(1),
                   FirstAt = StorageTime.FromNanoseconds(reader.GetInt64(2)),
                   LastAt = StorageTime.FromNanoseconds(reader.GetInt64(3))
               };
    }

    /// <summary>
    /// Maps a row of the start counts.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The agent ID, the outcome and the number of starts</returns>
    private static (string AgentId, ListenerOutcome Outcome, long Count) ReadStartCount(SqliteDataReader reader)
    {
        return (reader.GetString(0), ParseOutcome(reader.GetString(1)), reader.GetInt64(2));
    }

    /// <summary>
    /// Adds the start counts of an agent to its tally.
    /// </summary>
    /// <param name="agent">The tally of the agent without start counts</param>
    /// <param name="starts">The start counts of all listed agents</param>
    /// <returns>The tally</returns>
    private static ListenerTally WithStarts(ListenerTally agent, IReadOnlyList<(string AgentId, ListenerOutcome Outcome, long Count)> starts)
    {
        var own = starts.Where(entry => entry.AgentId == agent.AgentId).ToList();

        return new ListenerTally
               {
                   AgentId = agent.AgentId,
                   Snapshots = agent.Snapshots,
                   FirstAt = agent.FirstAt,
                   LastAt = agent.LastAt,
                   Starts = own.Sum(entry => entry.Count),
                   ComparedAll = own.Where(entry => entry.Outcome == ListenerOutcome.AllPorts).Sum(entry => entry.Count),
                   ComparedPrivileged = own.Where(entry => entry.Outcome == ListenerOutcome.PrivilegedOnly).Sum(entry => entry.Count),
                   NotCompared = own.Where(entry => IsNotCompared(entry.Outcome)).Sum(entry => entry.Count)
               };
    }

    /// <summary>
    /// Maps a finding row of either kind.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <param name="kind">The kind of the query</param>
    /// <returns>The finding</returns>
    private static ListenerFinding ReadFinding(SqliteDataReader reader, ListenerFindingKind kind)
    {
        var count = reader.GetInt64(5);
        var listeners = ReadListeners(reader);

        return new ListenerFinding
               {
                   Kind = kind,
                   AgentId = reader.GetString(0),
                   Seq = (ulong)reader.GetInt64(1),
                   CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(2)),
                   BaseCapturedAt = reader.IsDBNull(3) ? null : StorageTime.FromNanoseconds(reader.GetInt64(3)),
                   Outcome = kind == ListenerFindingKind.Agent ? AgentOutcome(reader.GetString(4)) : ParseOutcome(reader.GetString(4)),
                   Listeners = listeners,
                   Omitted = count - listeners.Count
               };
    }

    /// <summary>
    /// Reads the stored listener list of a finding row.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The listeners</returns>
    /// <exception cref="StoreException">The stored text does not decode</exception>
    private static List<Listener> ReadListeners(SqliteDataReader reader)
    {
        if (reader.IsDBNull(6))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Listener>>(reader.GetString(6), PayloadRegistry.Options) ?? [];
        }
        catch (JsonException exception)
        {
            throw new StoreException($"{Context}: decoding listeners", exception);
        }
    }

    /// <summary>
    /// Returns the outcome of an agent finding: the ports the agent compared.
    /// </summary>
    /// <param name="check">The listener check of the snapshot</param>
    /// <returns>The outcome</returns>
    private static ListenerOutcome AgentOutcome(string check)
    {
        return check == CheckAll ? ListenerOutcome.AllPorts : ListenerOutcome.PrivilegedOnly;
    }

    /// <summary>
    /// Reads an outcome from its stored text; an unknown text reads as not comparable.
    /// </summary>
    /// <param name="text">The stored text</param>
    /// <returns>The outcome</returns>
    private static ListenerOutcome ParseOutcome(string text)
    {
        return text switch
               {
                   NoEarlierText => ListenerOutcome.NoEarlierInventory,
                   PrivilegedIncompleteText => ListenerOutcome.PrivilegedIncomplete,
                   AllPortsText => ListenerOutcome.AllPorts,
                   PrivilegedOnlyText => ListenerOutcome.PrivilegedOnly,
                   _ => ListenerOutcome.NotComparable
               };
    }

    #endregion // Methods
}