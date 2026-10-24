using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// The socket overview at one moment.
/// </summary>
public sealed class ConnectionSnapshot : IPayload
{
    #region Fields

    private static readonly string[] _tcpStates = [
                                                      "ESTABLISHED",
                                                      "SYN_SENT",
                                                      "SYN_RECV",
                                                      "FIN_WAIT1",
                                                      "FIN_WAIT2",
                                                      "TIME_WAIT",
                                                      "CLOSE",
                                                      "CLOSE_WAIT",
                                                      "LAST_ACK",
                                                      "LISTEN",
                                                      "CLOSING",
                                                      "NEW_SYN_RECV"
                                                  ];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets a value indicating whether the overview is complete.
    /// </summary>
    [JsonPropertyName("complete")]
    public bool Complete { get; set; }

    /// <summary>
    /// Gets or sets the sockets per protocol and state.
    /// </summary>
    [JsonPropertyName("states")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StateCount>? States { get; set; }

    /// <summary>
    /// Gets or sets the sockets per process.
    /// </summary>
    [JsonPropertyName("processes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProcessConnections>? Processes { get; set; }

    /// <summary>
    /// Gets or sets the sockets per remote address.
    /// </summary>
    [JsonPropertyName("remotes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RemoteCount>? Remotes { get; set; }

    /// <summary>
    /// Gets or sets the listening sockets.
    /// </summary>
    [JsonPropertyName("listeners")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Listener>? Listeners { get; set; }

    /// <summary>
    /// Gets or sets the connected sockets.
    /// </summary>
    [JsonPropertyName("connections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Connection>? Connections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the snapshot holds the aggregates only.
    /// </summary>
    [JsonPropertyName("aggregates_only")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AggregatesOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the aggregates are incomplete.
    /// </summary>
    [JsonPropertyName("aggregates_incomplete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AggregatesIncomplete { get; set; }

    /// <summary>
    /// Gets or sets how far the listener inventory was compared with the previous run.
    /// </summary>
    [JsonPropertyName("listener_check")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListenerCheck
    {
        get;
        set => field = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Gets or sets the listeners that appeared since the previous run.
    /// </summary>
    [JsonPropertyName("new_listeners")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Listener>? NewListeners { get; set; }

    /// <summary>
    /// Gets or sets the number of new listeners that were left out of the list.
    /// </summary>
    [JsonPropertyName("new_listeners_omitted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint NewListenersOmitted { get; set; }

    /// <summary>
    /// Gets or sets how much of the listener inventory the snapshot holds.
    /// </summary>
    [JsonPropertyName("inventory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Inventory
    {
        get;
        set => field = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Gets or sets the protocols of the tables the run read.
    /// </summary>
    [JsonPropertyName("tables")]
    [JsonConverter(typeof(NullElementStringListConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Tables { get; set; }

    /// <summary>
    /// Gets or sets the kills that caused this snapshot, <c>null</c> for a scheduled one.
    /// </summary>
    [JsonPropertyName("trigger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnapshotTrigger? Trigger { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the protocol alone.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="proto">The protocol</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal static FieldError? CheckProtoOnly(string path, string proto)
    {
        return Check.OneOf($"{path}.proto", proto, Proto.Tcp, Proto.Tcp6, Proto.Udp, Proto.Udp6);
    }

    /// <summary>
    /// Checks the protocol and the state: the state is required for TCP and optional for UDP.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="proto">The protocol</param>
    /// <param name="state">The state</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal static FieldError? CheckProto(string path, string proto, string state)
    {
        var error = CheckProtoOnly(path, proto);

        if (error is not null)
        {
            return error;
        }

        if (state.Length == 0)
        {
            return proto == Proto.Udp || proto == Proto.Udp6 ? null : Check.Invalid($"{path}.state", "required");
        }

        return Check.OneOf($"{path}.state", state, _tcpStates);
    }

    /// <summary>
    /// Requires a count of at least 1.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="count">The count</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? CheckCount(string path, uint count)
    {
        return count < 1 ? Check.Invalid($"{path}.count", "must be at least 1") : null;
    }

    /// <summary>
    /// Checks the process ID and the command of an owner.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="pid">The process ID</param>
    /// <param name="command">The command</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal static FieldError? CheckProcess(string path, int pid, string command)
    {
        return pid < 0 ? Check.Invalid($"{path}.pid", "must not be negative") : Check.Short($"{path}.command", command);
    }

    /// <summary>
    /// Checks the UID, the user name and the mixed-UIDs flag of an entry.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="uid">The UID; <c>null</c> when unknown</param>
    /// <param name="user">The user name</param>
    /// <param name="mixedUids">Whether sockets of several UIDs share the endpoint</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
#pragma warning disable RH2003, S2325
    internal static FieldError? CheckSocketUser(string path, uint? uid, string user, bool mixedUids)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003, S2325

    /// <summary>
    /// Checks every entry of a list.
    /// </summary>
    /// <typeparam name="TItem">Type of the entries</typeparam>
    /// <param name="name">Name of the list</param>
    /// <param name="items">The entries; may be <c>null</c></param>
    /// <param name="validate">Checks one entry given its path</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private static FieldError? ValidateList<TItem>(string name, List<TItem>? items, Func<TItem, string, FieldError?> validate)
    {
        for (var index = 0; index < (items?.Count ?? 0); index++)
        {
            var error = validate(items![index], Check.Indexed(name, index));

            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the inventory state and the protocols of the tables the run read.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateInventory()
    {
        if (Inventory is not null)
        {
            var error = Check.OneOf("inventory", Inventory, "all", "privileged", "cut", "skipped");

            if (error is not null)
            {
                return error;
            }
        }

        var seen = new HashSet<string>();

        for (var index = 0; index < (Tables?.Count ?? 0); index++)
        {
            var path = Check.Indexed("tables", index);
            var table = Tables![index];
            var error = Check.OneOf(path, table, Proto.Tcp, Proto.Tcp6, Proto.Udp, Proto.Udp6);

            if (error is not null)
            {
                return error;
            }

            if (seen.Add(table))
            {
                continue;
            }

            return Check.Invalid(path, "duplicate protocol");
        }

        return null;
    }

    /// <summary>
    /// Checks the rules that tie the aggregate flags, the listener check and the new listeners together.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateFlags()
    {
        if (AggregatesOnly && Complete)
        {
            return Check.Invalid("complete", "must be false for an aggregates-only snapshot");
        }

        if (AggregatesOnly && (Connections?.Count ?? 0) > 0)
        {
            return Check.Invalid("connections", "must be empty in an aggregates-only snapshot");
        }

        if (AggregatesIncomplete && Complete)
        {
            return Check.Invalid("complete", "must be false when aggregates are incomplete");
        }

        if (ListenerCheck is not null)
        {
            var error = Check.OneOf("listener_check", ListenerCheck, "all", "privileged", "none", "start");

            if (error is not null)
            {
                return error;
            }
        }

        var reported = (NewListeners?.Count ?? 0) > 0 || NewListenersOmitted > 0;

        return reported && ListenerCheck != "all" && ListenerCheck != "privileged"
                   ? Check.Invalid("listener_check", "must be all or privileged when new listeners are reported")
                   : null;
    }

    #endregion // Methods

    #region IPayload

    /// <inheritdoc />
    [JsonIgnore]
    public string Kind => RecordKind.ConnectionSnapshot;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.Count("states", States?.Count ?? 0)
                        ?? Check.Count("processes", Processes?.Count ?? 0)
                        ?? Check.Count("remotes", Remotes?.Count ?? 0)
                        ?? Check.Count("listeners", Listeners?.Count ?? 0)
                        ?? Check.Count("connections", Connections?.Count ?? 0)
                        ?? Check.Count("new_listeners", NewListeners?.Count ?? 0)
                        ?? ValidateFlags()
                        ?? ValidateInventory();

        return error
                   ?? ValidateList("states", States, (item, path) => item.Validate(path))
                   ?? ValidateList("processes", Processes, (item, path) => item.Validate(path))
                   ?? ValidateList("remotes", Remotes, (item, path) => item.Validate(path))
                   ?? ValidateList("listeners", Listeners, (item, path) => item.Validate(path))
                   ?? ValidateList("connections", Connections, (item, path) => item.Validate(path))
                   ?? ValidateList("new_listeners", NewListeners, (item, path) => item.Validate(path))
                   ?? Trigger?.Validate("trigger");
    }

    #endregion // IPayload
}