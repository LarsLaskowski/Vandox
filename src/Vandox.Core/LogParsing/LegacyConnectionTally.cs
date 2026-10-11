using System.Net;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Counts the sockets of one lsof section.
/// </summary>
internal sealed class LegacyConnectionTally
{
    #region Fields

    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Proto, string State), uint> _states = [];
    private readonly Dictionary<(int Pid, string Command), int> _processIndex = [];
    private readonly List<ProcessConnections> _processes = [];
    private readonly Dictionary<IPAddress, uint> _remotes = [];
    private readonly List<Listener> _listeners = [];
    private readonly List<Connection> _connections = [];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Counts a socket line: the process always, the socket once.
    /// </summary>
    /// <param name="line">The line</param>
    internal void Add(LegacyLsofLine line)
    {
        CountProcess(line);

        if (_keys.Add(line.SocketKey))
        {
            CountSocket(line);
        }
    }

    /// <summary>
    /// Builds the connection snapshot.
    /// </summary>
    /// <param name="complete">Whether no line was left out and the section was closed by a marker or header</param>
    /// <returns>The snapshot; lists without entries are left out</returns>
    internal ConnectionSnapshot Build(bool complete)
    {
        return new ConnectionSnapshot
               {
                   Complete = complete,
                   States = OrNull(_states.OrderBy(pair => pair.Key.Proto, StringComparer.Ordinal)
                                          .ThenBy(pair => pair.Key.State, StringComparer.Ordinal)
                                          .Select(pair => new StateCount
                                                          {
                                                              Proto = pair.Key.Proto,
                                                              State = pair.Key.State,
                                                              Count = pair.Value
                                                          })
                                          .ToList()),
                   Processes = OrNull(_processes),
                   Remotes = OrNull(_remotes.Select(pair => (Address: pair.Key, Text: pair.Key.ToString(), Count: pair.Value))
                                            .OrderByDescending(item => item.Count)
                                            .ThenBy(item => item.Text, StringComparer.Ordinal)
                                            .Select(item => new RemoteCount
                                                            {
                                                                Addr = item.Address,
                                                                Count = item.Count
                                                            })
                                            .ToList()),
                   Listeners = OrNull(_listeners),
                   Connections = OrNull(_connections)
               };
    }

    /// <summary>
    /// Returns a list, or <c>null</c> when it has no entry.
    /// </summary>
    /// <typeparam name="TItem">The type of the entries</typeparam>
    /// <param name="items">The list</param>
    /// <returns>The list; <c>null</c> when empty</returns>
    private static List<TItem>? OrNull<TItem>(List<TItem> items)
    {
        return items.Count == 0 ? null : items;
    }

    /// <summary>
    /// Counts a line for its process.
    /// </summary>
    /// <param name="line">The line</param>
    private void CountProcess(LegacyLsofLine line)
    {
        if (_processIndex.TryGetValue((line.Pid, line.Command), out var position))
        {
            _processes[position].Count++;

            return;
        }

        _processIndex[(line.Pid, line.Command)] = _processes.Count;
        _processes.Add(new ProcessConnections
                       {
                           Pid = line.Pid,
                           Command = line.Command,
                           Count = 1
                       });
    }

    /// <summary>
    /// Counts a socket that no earlier line had.
    /// </summary>
    /// <param name="line">The line of its first owner</param>
    private void CountSocket(LegacyLsofLine line)
    {
        var key = (line.Proto, line.State);

        _states[key] = _states.GetValueOrDefault(key) + 1;

        if (line.Remote is null)
        {
            _listeners.Add(new Listener
                           {
                               Proto = line.Proto,
                               Local = line.Local,
                               Pid = line.Pid,
                               Command = line.Command
                           });

            return;
        }

        _remotes[line.Remote.Address] = _remotes.GetValueOrDefault(line.Remote.Address) + 1;
        _connections.Add(new Connection
                         {
                             Proto = line.Proto,
                             Local = line.Local,
                             Remote = line.Remote,
                             State = line.State,
                             Pid = line.Pid,
                             Command = line.Command
                         });
    }

    #endregion // Methods
}