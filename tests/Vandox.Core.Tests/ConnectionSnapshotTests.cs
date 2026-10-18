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
    public void ConnectionSnapshotSerializeOmitsAbsentNewFields(string json)
    {
        // Arrange
        var snapshot = Parse(json);

        // Act
        var written = JsonNode.Parse(PayloadRegistry.Serialize(snapshot))?.AsObject();

        // Assert
        Assert.IsNotNull(written, "the payload is written as an object");

        foreach (var key in new[] { "aggregates_only", "aggregates_incomplete", "listener_check", "new_listeners", "new_listeners_omitted" })
        {
            Assert.IsFalse(written.ContainsKey(key), $"{key} is absent from {written.ToJsonString()}");
        }

        Assert.IsNull(snapshot.ListenerCheck, "an absent listener check reads as null");
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