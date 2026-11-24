using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Builds the charts, the table and the notes of the network view from the rollups.
/// </summary>
public static class NetworkChartView
{
    #region Methods

    /// <summary>
    /// Builds the chart of the connection states.
    /// </summary>
    /// <param name="result">The rollups</param>
    /// <returns>The chart</returns>
    public static HistoryChartView StateChart(ConnectionRollupResult result)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds the chart of the sockets per program.
    /// </summary>
    /// <param name="result">The rollups</param>
    /// <returns>The chart</returns>
    public static HistoryChartView ProgramChart(ConnectionRollupResult result)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds the table of the remote addresses.
    /// </summary>
    /// <param name="result">The addresses</param>
    /// <returns>The table</returns>
    public static SnapshotTableView AddressTable(ConnectionAddressResult result)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds the notes of the table of the remote addresses.
    /// </summary>
    /// <param name="result">The addresses</param>
    /// <returns>The notes</returns>
    public static IReadOnlyList<string> AddressTableNotes(ConnectionAddressResult result)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    /// <summary>
    /// Builds the notes of the charts.
    /// </summary>
    /// <param name="rollups">The rollups</param>
    /// <param name="sources">The sources of the range</param>
    /// <param name="range">The range</param>
    /// <param name="stepLength">The length of a step</param>
    /// <returns>The notes</returns>
    public static IReadOnlyList<string> ChartNotes(ConnectionRollupResult rollups, ProgramSourceList sources, HistoryRange range, TimeSpan stepLength)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // Methods
}