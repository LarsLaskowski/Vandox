using System.Globalization;
using System.Net;
using System.Text;

using Vandox.Core.Model;
using Vandox.Core.Reports;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds connection snapshots, records and batches for the listener tests.
/// </summary>
internal static class ListenerSamples
{
    #region Constants

    /// <summary>
    /// The protocols of the four tables of /proc/net.
    /// </summary>
    internal static readonly string[] AllTables = [Proto.Tcp, Proto.Tcp6, Proto.Udp, Proto.Udp6];

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a listener.
    /// </summary>
    /// <param name="proto">The protocol</param>
    /// <param name="address">The local address</param>
    /// <param name="port">The local port</param>
    /// <param name="pid">The PID of the owner, or 0</param>
    /// <param name="command">The command of the owner</param>
    /// <returns>The listener</returns>
    internal static Listener Listener(string proto, string address, int port, int pid = 0, string command = "")
    {
        return new Listener
               {
                   Proto = proto,
                   Local = new IPEndPoint(IPAddress.Parse(address), port),
                   Pid = pid,
                   Command = command
               };
    }

    /// <summary>
    /// Creates a TCP listener on IPv4.
    /// </summary>
    /// <param name="address">The local address</param>
    /// <param name="port">The local port</param>
    /// <param name="pid">The PID of the owner, or 0</param>
    /// <param name="command">The command of the owner</param>
    /// <returns>The listener</returns>
    internal static Listener Tcp(string address, int port, int pid = 0, string command = "")
    {
        return Listener(Proto.Tcp, address, port, pid, command);
    }

    /// <summary>
    /// Creates a usable snapshot: all four tables and the given inventory state.
    /// </summary>
    /// <param name="check">The listener check</param>
    /// <param name="inventory">The inventory state</param>
    /// <param name="listeners">The inventory</param>
    /// <returns>The snapshot</returns>
    internal static ConnectionSnapshot Snapshot(string check, string inventory, params Listener[] listeners)
    {
        return new ConnectionSnapshot
               {
                   AggregatesOnly = true,
                   ListenerCheck = check,
                   Inventory = inventory,
                   Tables = [.. AllTables],
                   Listeners = [.. listeners]
               };
    }

    /// <summary>
    /// Creates a snapshot that carries the agent's own findings.
    /// </summary>
    /// <param name="check">The listener check, <c>all</c> or <c>privileged</c></param>
    /// <param name="found">The new listeners the agent lists</param>
    /// <param name="omitted">The number the agent left out</param>
    /// <returns>The snapshot</returns>
    internal static ConnectionSnapshot Found(string check, List<Listener> found, uint omitted)
    {
        var snapshot = Snapshot(check, check == "all" ? "all" : "privileged", Tcp("0.0.0.0", 22));

        snapshot.NewListeners = found;
        snapshot.NewListenersOmitted = omitted;

        return snapshot;
    }

    /// <summary>
    /// Creates an agent record of source <c>connections</c>.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="seconds">Seconds after <see cref="Samples.Base"/></param>
    /// <param name="snapshot">The snapshot</param>
    /// <returns>The record</returns>
    internal static DataRecord Record(ulong seq, int seconds, ConnectionSnapshot snapshot)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Agent,
                   Source = "connections",
                   Seq = seq,
                   CapturedAt = Samples.Base.AddSeconds(seconds),
                   Data = snapshot
               };
    }

    /// <summary>
    /// Creates an agent batch of one agent.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <param name="records">The records</param>
    /// <returns>The batch</returns>
    internal static RecordBatch Batch(string agentId, params DataRecord[] records)
    {
        var batch = Samples.AgentBatch(records);

        batch.AgentId = agentId;

        return batch;
    }

    /// <summary>
    /// Writes the protocol and endpoint of each listener of a finding, as the report shows them.
    /// </summary>
    /// <param name="finding">The finding</param>
    /// <returns>The listeners, such as <c>tcp 0.0.0.0:23</c></returns>
    internal static string[] Keys(ListenerFinding finding)
    {
        return [.. finding.Listeners.Select(listener => $"{listener.Proto} {listener.Local}")];
    }

    /// <summary>
    /// Writes the listeners of a finding as one text, separated by commas.
    /// </summary>
    /// <param name="finding">The finding</param>
    /// <returns>The listeners, such as <c>tcp 0.0.0.0:23,udp 0.0.0.0:69</c></returns>
    internal static string Joined(ListenerFinding finding)
    {
        return string.Join(",", Keys(finding));
    }

    /// <summary>
    /// Describes a page of findings as one text, to compare two pages.
    /// </summary>
    /// <param name="page">The page</param>
    /// <returns>The text</returns>
    internal static string Describe(ListenerFindingPage page)
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"total {page.Total}");

        foreach (var finding in page.Findings)
        {
            text.Append(CultureInfo.InvariantCulture, $"|{finding.Kind} {finding.AgentId} {finding.Seq} {finding.CapturedAt:O} {finding.BaseCapturedAt:O} {finding.Outcome} {finding.Omitted} [{string.Join(",", Keys(finding))}]");
        }

        return text.ToString();
    }

    /// <summary>
    /// Describes the tallies as one text, to compare two reads.
    /// </summary>
    /// <param name="page">The page</param>
    /// <returns>The text</returns>
    internal static string Describe(ListenerTallyPage page)
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"omitted {page.Omitted}");

        foreach (var tally in page.Agents)
        {
            text.Append(CultureInfo.InvariantCulture, $"|{tally.AgentId} {tally.Snapshots} {tally.FirstAt:O} {tally.LastAt:O} {tally.Starts} {tally.ComparedAll} {tally.ComparedPrivileged} {tally.NotCompared}");
        }

        return text.ToString();
    }

    #endregion // Methods
}