using System.Net;

using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Sample rollup results, listeners and connection records for the tests of the network view
/// </summary>
internal static class NetworkSamples
{
    #region Methods

    /// <summary>
    /// Creates a rollup point of an average with half of it as the minimum and twice of it as the maximum.
    /// </summary>
    /// <param name="average">The average</param>
    /// <returns>The point</returns>
    internal static ProgramRollupPoint Point(double average)
    {
        return new ProgramRollupPoint(average, average / 2, average * 2);
    }

    /// <summary>
    /// Creates a rollup line whose points have the given averages, a gap for <c>null</c>.
    /// </summary>
    /// <param name="name">The name of the state or program</param>
    /// <param name="averages">The averages per step</param>
    /// <returns>The line</returns>
    internal static ConnectionRollupLine Line(string name, params double?[] averages)
    {
        return new ConnectionRollupLine
               {
                   Name = name,
                   Points = [.. averages.Select(average => average is null ? (ProgramRollupPoint?)null : Point(average.Value))]
               };
    }

    /// <summary>
    /// Creates a rollup result of ten-minute steps from the start, as many as the longest line has points.
    /// </summary>
    /// <param name="start">The first step start</param>
    /// <param name="states">The state lines</param>
    /// <param name="programs">The program lines</param>
    /// <param name="others">The other programs per step, or <c>null</c> for 1 in every step</param>
    /// <returns>The result</returns>
    internal static ConnectionRollupResult Rollups(DateTimeOffset start, IReadOnlyList<ConnectionRollupLine> states, IReadOnlyList<ConnectionRollupLine> programs, IReadOnlyList<double?>? others = null)
    {
        var count = Math.Max(2, states.Concat(programs).Select(line => line.Points.Count).DefaultIfEmpty(0).Max());

        return new ConnectionRollupResult
               {
                   Times = ProcessesSamples.Times(start, TimeSpan.FromMinutes(10), count),
                   States = states,
                   Programs = programs,
                   Others = others ?? [.. Enumerable.Repeat((double?)1, count)],
                   Incomplete = [.. Enumerable.Repeat(0, count)]
               };
    }

    /// <summary>
    /// Creates a remote address of the rollups.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="average">The average connections</param>
    /// <param name="maximum">The highest count in one snapshot</param>
    /// <returns>The address</returns>
    internal static ConnectionAddress Address(string address, double average, double maximum)
    {
        return new ConnectionAddress
               {
                   Address = address,
                   Average = average,
                   Maximum = maximum
               };
    }

    /// <summary>
    /// Creates an address result of two ten-minute steps without gaps, caps or incomplete snapshots.
    /// </summary>
    /// <param name="start">The first step start</param>
    /// <param name="addresses">The addresses</param>
    /// <returns>The result</returns>
    internal static ConnectionAddressResult Addresses(DateTimeOffset start, params ConnectionAddress[] addresses)
    {
        return new ConnectionAddressResult
               {
                   Times = ProcessesSamples.Times(start, TimeSpan.FromMinutes(10), 2),
                   Gaps = [false, false],
                   Addresses = addresses,
                   Incomplete = [0, 0],
                   Capped = [0, 0]
               };
    }

    /// <summary>
    /// Creates a listener.
    /// </summary>
    /// <param name="proto">The protocol</param>
    /// <param name="local">The local endpoint</param>
    /// <param name="pid">The PID of the owner, 0 for none</param>
    /// <param name="command">The command of the owner</param>
    /// <param name="uid">The socket UID, or <c>null</c> when unknown</param>
    /// <param name="user">The user name</param>
    /// <param name="mixed">Whether sockets of several UIDs share the endpoint</param>
    /// <returns>The listener</returns>
    internal static Listener Listener(string proto, string local, int pid = 0, string command = "", uint? uid = null, string user = "", bool mixed = false)
    {
        return new Listener
               {
                   Proto = proto,
                   Local = IPEndPoint.Parse(local),
                   Pid = pid,
                   Command = command,
                   Uid = uid,
                   User = user,
                   MixedUids = mixed
               };
    }

    /// <summary>
    /// Creates a TCP listener on a port of all addresses without an owner.
    /// </summary>
    /// <param name="port">The port</param>
    /// <returns>The listener</returns>
    internal static Listener Tcp(int port)
    {
        return Listener("tcp", $"0.0.0.0:{port}");
    }

    /// <summary>
    /// Creates a stored connection snapshot with a listener inventory.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="inventory">The inventory state, or <c>null</c> for a snapshot of an import or a 1.0 agent</param>
    /// <param name="listeners">The listeners</param>
    /// <returns>The record</returns>
    internal static StoredRecord Connections(long id, string source, DateTimeOffset capturedAt, string? inventory, params Listener[] listeners)
    {
        return HistorySamples.Record(id,
                                     source,
                                     capturedAt,
                                     new ConnectionSnapshot
                                     {
                                         Complete = true,
                                         Inventory = inventory,
                                         Tables = inventory is null ? null : ["tcp", "tcp6", "udp", "udp6"],
                                         ListenerCheck = inventory is null ? null : "start",
                                         Listeners = [.. listeners]
                                     });
    }

    /// <summary>
    /// Joins the texts of the cells of a row with a vertical bar.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The texts</returns>
    internal static string Cells(SnapshotRowView row)
    {
        return string.Join('|', row.Cells.Select(HistorySamples.Text));
    }

    /// <summary>
    /// Tells whether any run of a cell is escaped.
    /// </summary>
    /// <param name="runs">The runs</param>
    /// <returns><c>true</c> when a run is escaped</returns>
    internal static bool AnyEscaped(IReadOnlyList<TextRun> runs)
    {
        return runs.Any(run => run.Escaped);
    }

    #endregion // Methods
}