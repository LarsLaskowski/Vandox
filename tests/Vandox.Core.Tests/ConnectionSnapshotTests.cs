using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ConnectionSnapshot"/>
/// </summary>
[TestClass]
public class ConnectionSnapshotTests
{
    #region Constants

    /// <summary>
    /// The reason of a new listener list or count without a listener check.
    /// </summary>
    private const string CheckNeeded = "must be all or privileged when new listeners are reported";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The fields of format 1.1 are accepted in the combinations the rules allow.
    /// </summary>
    /// <param name="json">The payload</param>
    [TestMethod]
    [DataRow("""{"complete":false,"aggregates_incomplete":true}""")]
    [DataRow("""{"complete":false,"aggregates_only":true,"states":[{"proto":"udp","count":1}],"listeners":[{"proto":"tcp","local":"0.0.0.0:22"}]}""")]
    [DataRow("""{"complete":true,"listener_check":"all"}""")]
    [DataRow("""{"complete":true,"listener_check":"privileged"}""")]
    [DataRow("""{"complete":true,"listener_check":"none"}""")]
    [DataRow("""{"complete":false,"listener_check":"all","new_listeners":[{"proto":"tcp","local":"0.0.0.0:23","pid":7,"command":"telnetd"}],"new_listeners_omitted":3}""")]
    [DataRow("""{"complete":false,"listener_check":"privileged","new_listeners_omitted":1}""")]
    [DataRow("""{"complete":false,"aggregates_only":true,"aggregates_incomplete":true,"listener_check":"privileged","new_listeners":[{"proto":"udp6","local":"[::]:53"}]}""")]
    [DataRow("""{"complete":false,"listener_check":"all","new_listeners":[],"new_listeners_omitted":0}""")]
    [DataRow("""{"complete":true,"listener_check":"start"}""")]
    [DataRow("""{"complete":false,"listener_check":"start","inventory":"privileged","tables":["tcp","udp"]}""")]
    [DataRow("""{"complete":true,"inventory":"all"}""")]
    [DataRow("""{"complete":true,"inventory":"privileged"}""")]
    [DataRow("""{"complete":true,"inventory":"cut"}""")]
    [DataRow("""{"complete":true,"inventory":"skipped"}""")]
    [DataRow("""{"complete":true,"tables":["udp6"]}""")]
    [DataRow("""{"complete":true,"tables":["tcp","tcp6","udp","udp6"]}""")]
    public void ConnectionSnapshotValidateAcceptsTheNewFields(string json)
    {
        // Arrange
        var snapshot = Parse(json);

        // Act
        var error = snapshot.Validate();

        // Assert
        Assert.IsNull(error, $"the rules allow {json}, got {error?.Field}: {error?.Reason}");
    }

    /// <summary>
    /// A broken rule of the fields of format 1.1 names the field and the reason.
    /// </summary>
    /// <param name="json">The payload</param>
    /// <param name="field">The expected field</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("""{"complete":true,"aggregates_only":true}""", "complete", "must be false for an aggregates-only snapshot")]
    [DataRow("""{"complete":false,"aggregates_only":true,"connections":[{"proto":"udp","local":"10.0.0.1:1","remote":"10.0.0.2:2"}]}""", "connections", "must be empty in an aggregates-only snapshot")]
    [DataRow("""{"complete":true,"aggregates_incomplete":true}""", "complete", "must be false when aggregates are incomplete")]
    [DataRow("""{"complete":false,"listener_check":"bogus"}""", "listener_check", "unknown value")]
    [DataRow("""{"complete":false,"listener_check":"ALL"}""", "listener_check", "unknown value")]
    [DataRow("""{"complete":false,"new_listeners":[{"proto":"tcp","local":"0.0.0.0:23"}]}""", "listener_check", CheckNeeded)]
    [DataRow("""{"complete":false,"listener_check":"none","new_listeners":[{"proto":"tcp","local":"0.0.0.0:23"}]}""", "listener_check", CheckNeeded)]
    [DataRow("""{"complete":false,"new_listeners_omitted":1}""", "listener_check", CheckNeeded)]
    [DataRow("""{"complete":false,"listener_check":"none","new_listeners_omitted":1}""", "listener_check", CheckNeeded)]
    [DataRow("""{"complete":false,"listener_check":"start","new_listeners":[{"proto":"tcp","local":"0.0.0.0:23"}]}""", "listener_check", CheckNeeded)]
    [DataRow("""{"complete":false,"listener_check":"start","new_listeners_omitted":1}""", "listener_check", CheckNeeded)]
    [DataRow("""{"complete":true,"inventory":"bogus"}""", "inventory", "unknown value")]
    [DataRow("""{"complete":true,"inventory":"ALL"}""", "inventory", "unknown value")]
    [DataRow("""{"complete":true,"tables":["tcp","sctp"]}""", "tables[1]", "unknown value")]
    [DataRow("""{"complete":true,"tables":["TCP"]}""", "tables[0]", "unknown value")]
    [DataRow("""{"complete":true,"tables":["tcp","udp","tcp"]}""", "tables[2]", "duplicate protocol")]
    [DataRow("""{"complete":true,"tables":["udp6","udp6"]}""", "tables[1]", "duplicate protocol")]
    [DataRow("""{"complete":false,"listener_check":"all","new_listeners":[{"proto":"sctp","local":"0.0.0.0:23"}]}""", "new_listeners[0].proto", "unknown value")]
    [DataRow("""{"complete":false,"listener_check":"all","new_listeners":[{"proto":"tcp"}]}""", "new_listeners[0].local", "invalid address")]
    [DataRow("""{"complete":false,"listener_check":"all","new_listeners":[{"proto":"tcp","local":"1.2.3.4:1","pid":-1}]}""", "new_listeners[0].pid", "must not be negative")]
    public void ConnectionSnapshotValidateRefusesBrokenFieldRules(string json, string field, string reason)
    {
        // Arrange
        var snapshot = Parse(json);

        // Act
        var error = snapshot.Validate();

        // Assert
        Assert.IsNotNull(error, "a broken rule is refused");
        Assert.AreEqual(field, error.Field, "field");
        Assert.AreEqual(reason, error.Reason, "reason");
    }

    /// <summary>
    /// More new listeners than a list may hold are refused, and the entries of the list are checked like the listeners.
    /// </summary>
    [TestMethod]
    public void ConnectionSnapshotValidateRefusesTooManyNewListeners()
    {
        // Arrange
        var listener = new Listener
                       {
                           Proto = "tcp",
                           Local = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 80)
                       };
        var atLimit = new ConnectionSnapshot
                      {
                          ListenerCheck = "all",
                          NewListeners = Enumerable.Repeat(listener, ModelLimits.MaxItems).ToList()
                      };
        var tooMany = new ConnectionSnapshot
                      {
                          ListenerCheck = "all",
                          NewListeners = Enumerable.Repeat(listener, ModelLimits.MaxItems + 1).ToList()
                      };

        // Act
        var limitError = atLimit.Validate();
        var error = tooMany.Validate();

        // Assert
        Assert.IsNull(limitError, "a list at the limit is accepted");
        Assert.IsNotNull(error, "a list over the limit is refused");
        Assert.AreEqual("new_listeners", error.Field, "field");
        Assert.AreEqual("too many entries", error.Reason, "reason");
    }

    /// <summary>
    /// An absent field of format 1.1 is not written, whether it was never set, set to its zero value or read as <c>null</c> or empty.
    /// </summary>
    /// <param name="json">The payload that was read</param>
    [TestMethod]
    [DataRow("""{"complete":true}""")]
    [DataRow("""{"complete":true,"listener_check":null}""")]
    [DataRow("""{"complete":true,"listener_check":""}""")]
    [DataRow("""{"complete":true,"aggregates_only":false,"aggregates_incomplete":false,"new_listeners":null,"new_listeners_omitted":0}""")]
    [DataRow("""{"complete":true,"inventory":null,"tables":null}""")]
    [DataRow("""{"complete":true,"inventory":""}""")]
    public void ConnectionSnapshotSerializeOmitsAbsentNewFields(string json)
    {
        // Arrange
        var snapshot = Parse(json);

        // Act
        var written = JsonNode.Parse(PayloadRegistry.Serialize(snapshot))?.AsObject();

        // Assert
        Assert.IsNotNull(written, "the payload is written as an object");

        foreach (var key in new[] { "aggregates_only", "aggregates_incomplete", "listener_check", "new_listeners", "new_listeners_omitted", "inventory", "tables" })
        {
            Assert.IsFalse(written.ContainsKey(key), $"{key} is absent from {written.ToJsonString()}");
        }

        Assert.IsNull(snapshot.ListenerCheck, "an absent listener check reads as null");
    }

    /// <summary>
    /// The socket's UID, user and mixed-UIDs flag are accepted in the combinations the rules allow.
    /// </summary>
    /// <param name="json">The payload</param>
    [TestMethod]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":0,"user":"root"}]}""")]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":4294967295,"user":"nobody"}]}""")]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":1001}]}""")]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","user":"","mixed_uids":false}]}""")]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":null,"user":null,"mixed_uids":null}]}""")]
    [DataRow("""{"complete":true,"listeners":[{"proto":"udp","local":"0.0.0.0:5353","uid":33,"user":"www-data","mixed_uids":true}]}""")]
    [DataRow("""{"complete":true,"listener_check":"all","new_listeners":[{"proto":"udp","local":"0.0.0.0:5353","uid":105,"user":"avahi","mixed_uids":true}]}""")]
    [DataRow("""{"complete":true,"connections":[{"proto":"tcp","local":"10.0.0.1:80","remote":"192.0.2.1:5000","state":"ESTABLISHED","uid":33,"user":"www-data"}]}""")]
    [DataRow("""{"complete":true,"connections":[{"proto":"tcp","local":"10.0.0.1:80","remote":"192.0.2.1:5000","state":"ESTABLISHED","mixed_uids":true}]}""")]
    public void ConnectionSnapshotValidateAcceptsTheSocketUser(string json)
    {
        // Arrange
        var snapshot = Parse(json);

        // Act
        var error = snapshot.Validate();

        // Assert
        Assert.IsNull(error, $"the socket user rules allow {json}, got {error?.Field}: {error?.Reason}");
    }

    /// <summary>
    /// A user or a mixed-UIDs flag without a UID, and a user over the limit, are refused with the field and the reason.
    /// </summary>
    /// <param name="json">The payload</param>
    /// <param name="field">The expected field</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","user":"root"}]}""", "listeners[0].user", "requires uid")]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","mixed_uids":true}]}""", "listeners[0].mixed_uids", "requires uid")]
    [DataRow("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","user":"root","mixed_uids":true}]}""", "listeners[0].user", "requires uid")]
    [DataRow("""{"complete":true,"listener_check":"all","new_listeners":[{"proto":"tcp","local":"0.0.0.0:22","user":"root"}]}""", "new_listeners[0].user", "requires uid")]
    [DataRow("""{"complete":true,"listener_check":"all","new_listeners":[{"proto":"tcp","local":"0.0.0.0:22","mixed_uids":true}]}""", "new_listeners[0].mixed_uids", "requires uid")]
    [DataRow("""{"complete":true,"connections":[{"proto":"tcp","local":"10.0.0.1:80","remote":"192.0.2.1:5000","state":"ESTABLISHED","user":"root"}]}""", "connections[0].user", "requires uid")]
    public void ConnectionSnapshotValidateChecksTheSocketUser(string json, string field, string reason)
    {
        // Arrange
        var snapshot = Parse(json);

        // Act
        var error = snapshot.Validate();

        // Assert
        Assert.IsNotNull(error, "a socket user that breaks a rule is refused");
        Assert.AreEqual(field, error.Field, "field");
        Assert.AreEqual(reason, error.Reason, "reason");
    }

    /// <summary>
    /// A user over the short-text limit is refused on listeners, new listeners and connections, and a user at the limit is accepted.
    /// </summary>
    [TestMethod]
    public void ConnectionSnapshotValidateRefusesATooLongSocketUser()
    {
        // Arrange
        var atLimit = new string('u', ModelLimits.MaxShortTextBytes);
        var tooLong = new string('u', ModelLimits.MaxShortTextBytes + 1);

        // Act
        var accepted = Parse($$"""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":5,"user":"{{atLimit}}"}]}""").Validate();
        var listener = Parse($$"""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":5,"user":"{{tooLong}}"}]}""").Validate();
        var newListener = Parse($$"""{"complete":true,"listener_check":"all","new_listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":5,"user":"{{tooLong}}"}]}""").Validate();
        var connection = Parse($$"""{"complete":true,"connections":[{"proto":"tcp","local":"10.0.0.1:80","remote":"192.0.2.1:5000","state":"ESTABLISHED","uid":5,"user":"{{tooLong}}"}]}""").Validate();

        // Assert
        Assert.IsNull(accepted, "a user at the limit is accepted");
        Assert.IsNotNull(listener, "a listener user over the limit is refused");
        Assert.AreEqual("listeners[0].user", listener.Field, "listener field");
        Assert.AreEqual("too long", listener.Reason, "listener reason");
        Assert.IsNotNull(newListener, "a new listener user over the limit is refused");
        Assert.AreEqual("new_listeners[0].user", newListener.Field, "new listener field");
        Assert.AreEqual("too long", newListener.Reason, "new listener reason");
        Assert.IsNotNull(connection, "a connection user over the limit is refused");
        Assert.AreEqual("connections[0].user", connection.Field, "connection field");
        Assert.AreEqual("too long", connection.Reason, "connection reason");
    }

    /// <summary>
    /// An absent UID, user and mixed-UIDs flag are not written, whether never set, read as <c>null</c> or empty; UID 0 is written.
    /// </summary>
    [TestMethod]
    public void ConnectionSnapshotSerializeOmitsAbsentSocketUser()
    {
        // Arrange
        var absent = Parse("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":null,"user":null,"mixed_uids":null},{"proto":"tcp","local":"0.0.0.0:23","user":"","mixed_uids":false}],"connections":[{"proto":"tcp","local":"10.0.0.1:80","remote":"192.0.2.1:5000","state":"ESTABLISHED","uid":null,"user":""}]}""");
        var root = Parse("""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":0,"user":"root","mixed_uids":true}],"connections":[{"proto":"tcp","local":"10.0.0.1:80","remote":"192.0.2.1:5000","state":"ESTABLISHED","uid":0}]}""");

        // Act
        var writtenAbsent = JsonNode.Parse(PayloadRegistry.Serialize(absent))?.AsObject();
        var writtenRoot = JsonNode.Parse(PayloadRegistry.Serialize(root))?.AsObject();

        // Assert
        Assert.IsNotNull(writtenAbsent, "the payload is written as an object");
        Assert.IsNotNull(writtenRoot, "the payload of UID 0 is written as an object");

        foreach (var entry in writtenAbsent["listeners"]!.AsArray().Concat(writtenAbsent["connections"]!.AsArray()))
        {
            foreach (var key in new[] { "uid", "user", "mixed_uids" })
            {
                Assert.IsFalse(entry!.AsObject().ContainsKey(key), $"{key} is absent from {entry.ToJsonString()}");
            }
        }

        var listener = writtenRoot["listeners"]![0]!.AsObject();
        var connection = writtenRoot["connections"]![0]!.AsObject();

        Assert.AreEqual(0, (int)listener["uid"]!, "listener: uid 0 is written");
        Assert.AreEqual("root", (string)listener["user"]!, "listener: user");
        Assert.IsTrue((bool)listener["mixed_uids"]!, "listener: mixed_uids");
        Assert.AreEqual(0, (int)connection["uid"]!, "connection: uid 0 is written");
        Assert.IsFalse(connection.ContainsKey("user"), "connection: an empty user is not written");
    }

    /// <summary>
    /// A UID that is out of range, negative, fractional or a string is a malformed payload.
    /// </summary>
    /// <param name="uid">The JSON text of the UID</param>
    [TestMethod]
    [DataRow("-1")]
    [DataRow("4294967296")]
    [DataRow("1.5")]
    [DataRow("\"0\"")]
    [DataRow("1e400")]
    [DataRow("-0")]
    public void ConnectionSnapshotDeserializeRefusesABadUid(string uid)
    {
        // Arrange
        using var document = JsonDocument.Parse($$"""{"complete":true,"listeners":[{"proto":"tcp","local":"0.0.0.0:22","uid":{{uid}}}]}""");

        // Act
        var exception = Assert.Throws<JsonException>(() => PayloadRegistry.Deserialize(RecordKind.ConnectionSnapshot, document.RootElement), $"uid {uid} is not a 32-bit unsigned number");

        // Assert
        Assert.IsNotNull(exception, "the payload is refused");
    }

    /// <summary>
    /// Reads a payload.
    /// </summary>
    /// <param name="json">The JSON text</param>
    /// <returns>The snapshot</returns>
    private static ConnectionSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return (ConnectionSnapshot)PayloadRegistry.Deserialize(RecordKind.ConnectionSnapshot, document.RootElement)!;
    }

    #endregion // Methods
}