using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An incident store that answers the incident queries with scripted pages, whatever the query asks, and stops a runaway loop.
/// </summary>
internal sealed class ScriptedIncidentStore : IIncidentStore
{
    #region Constants

    private const int MaxCalls = 50;

    #endregion // Constants

    #region Fields

    private readonly IReadOnlyList<IReadOnlyList<StoredIncident>> _pages;

    private readonly bool _repeatLast;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptedIncidentStore"/> class.
    /// </summary>
    /// <param name="pages">The answers to the queries, in order</param>
    /// <param name="repeatLast">Whether the last page answers every further query; otherwise further queries get an empty page</param>
    internal ScriptedIncidentStore(IReadOnlyList<IReadOnlyList<StoredIncident>> pages, bool repeatLast)
    {
        _pages = pages;
        _repeatLast = repeatLast;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of queries that were asked.
    /// </summary>
    internal int Calls { get; private set; }

    /// <summary>
    /// Gets or sets the number of the query that fails; 0 for none.
    /// </summary>
    internal int FailAtCall { get; set; }

    /// <summary>
    /// Gets or sets the failure of the query <see cref="FailAtCall"/>; a store exception when <c>null</c>.
    /// </summary>
    internal Exception? Failure { get; set; }

    #endregion // Properties

    #region IIncidentStore

    /// <inheritdoc />
    public Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        Calls++;

        if (Calls > MaxCalls)
        {
            throw new InvalidOperationException("the reader asked for more pages than the script has");
        }

        if (Calls == FailAtCall)
        {
            throw Failure ?? new StoreException("store: reading incidents: scripted failure");
        }

        if (Calls <= _pages.Count)
        {
            return Task.FromResult(_pages[Calls - 1]);
        }

        return Task.FromResult<IReadOnlyList<StoredIncident>>(_repeatLast ? _pages[^1] : []);
    }

    /// <inheritdoc />
    public Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    #endregion // IIncidentStore
}