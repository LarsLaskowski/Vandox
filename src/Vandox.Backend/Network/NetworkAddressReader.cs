using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the table of the top remote addresses.
/// </summary>
public sealed class NetworkAddressReader
{
    #region Fields

    /// <summary>
    /// The reader of the rollups.
    /// </summary>
    private readonly IConnectionRollupReader _reader;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkAddressReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the rollups</param>
    public NetworkAddressReader(IConnectionRollupReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the table of a source and a range.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="range">The range</param>
    /// <param name="step">The length of a step of the charts</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the table</returns>
    public async Task<NetworkAddresses> ReadAsync(string source, HistoryRange range, TimeSpan step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        var result = await _reader.ConnectionAddressesAsync(new ConnectionRollupQuery
                                                            {
                                                                Source = source,
                                                                From = range.Start,
                                                                To = range.End,
                                                                Step = step
                                                            },
                                                            cancellationToken).ConfigureAwait(false);

        return new NetworkAddresses
               {
                   Table = NetworkChartView.AddressTable(result),
                   Notes = NetworkChartView.AddressTableNotes(result)
               };
    }

    #endregion // Methods
}