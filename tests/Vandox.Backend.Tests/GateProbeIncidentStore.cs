using Vandox.Core.Incidents;
using Vandox.Storage;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// An incident store that counts its calls, the highest number of calls in flight and the query and token of the last read, and lets calls overlap while
/// they run.
/// </summary>
internal sealed class GateProbeIncidentStore : IIncidentStore
{
    #region Constants

    /// <summary>
    /// The count the store answers.
    /// </summary>
    internal const long Total = 42;

    #endregion // Constants

    #region Fields

    private int _inFlight;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of reads.
    /// </summary>
    internal int Calls { get; private set; }

    /// <summary>
    /// Gets the number of saves.
    /// </summary>
    internal int Saves { get; private set; }

    /// <summary>
    /// Gets the highest number of calls in flight.
    /// </summary>
    internal int MaxInFlight { get; private set; }

    /// <summary>
    /// Gets the queries of the incident reads.
    /// </summary>
    internal List<IncidentQuery> Queries { get; } = [];

    /// <summary>
    /// Gets the token of the last incident read.
    /// </summary>
    internal CancellationToken Token { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Counts a call and lets other calls overlap with it.
    /// </summary>
    /// <returns>A task that completes when the call is done</returns>
    private async Task EnterAsync()
    {
        Calls++;
        _inFlight++;
        MaxInFlight = Math.Max(MaxInFlight, _inFlight);

        await Task.Yield();

        _inFlight--;
    }

    #endregion // Methods

    #region IIncidentStore

    /// <inheritdoc />
    public Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        Saves++;

        return Task.FromResult(new IncidentSaveResult());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        Token = cancellationToken;

        await EnterAsync();

        return [Stored(1, Outage(KillAt, 1, "mariadbd"))];
    }

    /// <inheritdoc />
    public async Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        await EnterAsync();

        return Total;
    }

    #endregion // IIncidentStore
}