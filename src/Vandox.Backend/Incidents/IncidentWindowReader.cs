#pragma warning disable RH2003, S2325, S4487

using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Reads the data of the charts of an incident window from the stored records.
/// </summary>
public sealed class IncidentWindowReader
{
    #region Fields

    private readonly IRecordReader _records;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentWindowReader"/> class.
    /// </summary>
    /// <param name="records">The reader of stored records</param>
    public IncidentWindowReader(IRecordReader records)
    {
        ArgumentNullException.ThrowIfNull(records);

        _records = records;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the memory per program, the available memory and swap, and the connections of the window of an incident.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that returns the data of the charts</returns>
    /// <exception cref="StoreException">The store failed</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled</exception>
    public Task<IncidentWindowData> ReadAsync(Incident incident, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}