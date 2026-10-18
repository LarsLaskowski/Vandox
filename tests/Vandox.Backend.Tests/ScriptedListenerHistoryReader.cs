using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A reader of the listener history that answers with scripted pages, whatever the query asks, and keeps a copy of every query with its token. Without a
/// script it answers every read with an empty page.
/// </summary>
internal sealed class ScriptedListenerHistoryReader : IListenerHistoryReader
{
    #region Properties

    /// <summary>
    /// Gets or sets the answer to the read of the tallies.
    /// </summary>
    internal ListenerTallyPage Tallies { get; set; } = new();

    /// <summary>
    /// Gets or sets the answer to the read of the findings after an agent start.
    /// </summary>
    internal ListenerFindingPage StartFindings { get; set; } = new();

    /// <summary>
    /// Gets or sets the answer to the read of the findings of the agents.
    /// </summary>
    internal ListenerFindingPage AgentFindings { get; set; } = new();

    /// <summary>
    /// Gets or sets the failure of every read; none when <c>null</c>.
    /// </summary>
    internal Exception? Failure { get; set; }

    /// <summary>
    /// Gets the number of reads of the tallies.
    /// </summary>
    internal int TallyCalls { get; private set; }

    /// <summary>
    /// Gets the tokens of the reads of the tallies.
    /// </summary>
    internal List<CancellationToken> TallyTokens { get; } = [];

    /// <summary>
    /// Gets copies of the queries for findings, in order.
    /// </summary>
    internal List<ListenerFindingQuery> FindingQueries { get; } = [];

    /// <summary>
    /// Gets the tokens of the reads of findings, in order.
    /// </summary>
    internal List<CancellationToken> FindingTokens { get; } = [];

    #endregion // Properties

    #region IListenerHistoryReader

    /// <inheritdoc />
    public Task<ListenerTallyPage> ListenerTalliesAsync(CancellationToken cancellationToken)
    {
        TallyCalls++;
        TallyTokens.Add(cancellationToken);

        return Failure is null ? Task.FromResult(Tallies) : Task.FromException<ListenerTallyPage>(Failure);
    }

    /// <inheritdoc />
    public Task<ListenerFindingPage> ListenerFindingsAsync(ListenerFindingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        FindingQueries.Add(new ListenerFindingQuery
                           {
                               Kind = query.Kind,
                               Limit = query.Limit
                           });
        FindingTokens.Add(cancellationToken);

        if (Failure is not null)
        {
            return Task.FromException<ListenerFindingPage>(Failure);
        }

        return Task.FromResult(query.Kind == ListenerFindingKind.AgentStart ? StartFindings : AgentFindings);
    }

    #endregion // IListenerHistoryReader
}