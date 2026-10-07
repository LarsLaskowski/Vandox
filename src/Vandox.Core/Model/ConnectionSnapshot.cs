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

    #endregion // Methods

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.ConnectionSnapshot;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.Count("states", States?.Count ?? 0)
                        ?? Check.Count("processes", Processes?.Count ?? 0)
                        ?? Check.Count("remotes", Remotes?.Count ?? 0)
                        ?? Check.Count("listeners", Listeners?.Count ?? 0)
                        ?? Check.Count("connections", Connections?.Count ?? 0);

        return error
                   ?? ValidateList("states", States, (item, path) => item.Validate(path))
                   ?? ValidateList("processes", Processes, (item, path) => item.Validate(path))
                   ?? ValidateList("remotes", Remotes, (item, path) => item.Validate(path))
                   ?? ValidateList("listeners", Listeners, (item, path) => item.Validate(path))
                   ?? ValidateList("connections", Connections, (item, path) => item.Validate(path));
    }

    #endregion // IPayload
}