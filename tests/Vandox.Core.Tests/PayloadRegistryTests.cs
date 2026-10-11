using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="PayloadRegistry"/>
/// </summary>
[TestClass]
public class PayloadRegistryTests
{
    #region Methods

    /// <summary>
    /// A serialized payload of every kind has no <c>kind</c> member at the top level, whatever its case.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="json">The wire payload</param>
    [TestMethod]
    [DataRow(RecordKind.Metric, """{"name":"cpu.load","value":1.5,"unit":"percent","labels":{"cpu":"0"}}""")]
    [DataRow(RecordKind.ProcessSnapshot, """{"complete":true,"total":2,"processes":[{"pid":1,"ppid":0,"command":"init","state":"S","cpu_percent":0.5,"rss_bytes":10,"oom_score_adj":0}],"programs":[{"program":"init","count":1,"cpu_percent":0.5,"rss_bytes":10}]}""")]
    [DataRow(RecordKind.ConnectionSnapshot, """{"complete":true,"states":[{"proto":"tcp","state":"LISTEN","count":1}],"remotes":[{"addr":"10.0.0.2","count":1}],"listeners":[{"proto":"tcp6","local":"[::]:22","pid":3,"command":"sshd"}]}""")]
    [DataRow(RecordKind.ServiceState, """{"unit":"mariadb.service","load_state":"loaded","active_state":"active","sub_state":"running","active_enter_at":"2026-10-01T10:00:00Z","restarts":1}""")]
    [DataRow(RecordKind.MariaDbStatus, """{"availability":"up","ping_latency_ns":5,"status":{"Threads_connected":3},"variables":{"max_connections":"151"},"threads":[{"id":1,"user":"root","time_seconds":2,"info":"SELECT 1"}],"complete":true}""")]
    [DataRow(RecordKind.KernelEvent, """{"type":"oom_kill","oom_kill":{"victim_pid":42,"victim_command":"mysqld","anon_rss_bytes":100,"oom_score_adj":-100}}""")]
    [DataRow(RecordKind.LogLine, """{"log":"syslog","program":"sshd","pid":12,"priority":3,"message":"hello"}""")]
    [DataRow(RecordKind.Gap, """{"from":"2026-10-01T10:00:00Z","to":"2026-10-01T10:05:00Z","cause":"collector_timeout","collector":"proc"}""")]
    public void PayloadRegistrySerializeWritesNoKindMember(string kind, string json)
    {
        // Arrange
        var payload = PayloadRegistry.Deserialize(kind, JsonDocument.Parse(json).RootElement);

        // Act
        var text = PayloadRegistry.Serialize(payload!);

        // Assert
        using var document = JsonDocument.Parse(text);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        var kindNames = names.Where(name => string.Equals(name, "kind", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.IsGreaterThan(0, names.Count, "the payload is written as a non-empty object");
        Assert.IsEmpty(kindNames, $"no kind member at the top level of {text}");
    }

    /// <summary>
    /// Every record kind has a payload type, every concrete payload type of the assembly belongs to a kind, and each one keeps its <c>Kind</c> out of the JSON.
    /// </summary>
    [TestMethod]
    public void PayloadRegistryEveryPayloadTypeIgnoresKindProperty()
    {
        // Arrange
        var constants = typeof(RecordKind).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral && field.FieldType == typeof(string));
        var kinds = constants.Select(field => (string)field.GetRawConstantValue()!).ToList();
        var implementations = typeof(IPayload).Assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IPayload).IsAssignableFrom(type)).ToList();

        // Act
        var registered = kinds.Select(PayloadRegistry.TypeOf).ToList();

        // Assert
        Assert.IsGreaterThan(0, kinds.Count, "RecordKind has constants");
        Assert.IsEmpty(registered.Where(type => type is null).ToList(), "every record kind maps to a payload type");
        Assert.IsGreaterThan(0, implementations.Count, "the assembly has payload types");

        foreach (var implementation in implementations)
        {
            Assert.Contains(implementation, registered, $"payload type {implementation.Name} is the type of a record kind");
        }

        foreach (var type in registered.Concat(implementations).Distinct().OfType<Type>())
        {
            var property = type.GetProperty(nameof(IPayload.Kind), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.IsNotNull(property, $"Kind is declared on {type.Name}");

            var attribute = property.GetCustomAttribute<JsonIgnoreAttribute>();

            Assert.IsNotNull(attribute, $"Kind of {type.Name} carries JsonIgnore");
            Assert.AreEqual(JsonIgnoreCondition.Always, attribute.Condition, $"JsonIgnore condition of Kind of {type.Name}");
        }
    }

    /// <summary>
    /// A <c>Kind</c> member inside a payload is read without effect, also in the shape stored before the member was dropped.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="json">The payload, carrying a <c>Kind</c> member</param>
    [TestMethod]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"Kind":"metric"}""")]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"Kind":"gap"}""")]
    [DataRow(RecordKind.Metric, """{"name":"cpu","value":1,"kind":"gap"}""")]
    [DataRow(RecordKind.Gap, """{"from":"2026-03-01T09:00:00+00:00","to":"2026-03-01T10:00:00+00:00","cause":"unknown","collector":"","Kind":"gap"}""")]
    public void PayloadRegistryDeserializeIgnoresKindMember(string kind, string json)
    {
        // Arrange
        var element = JsonDocument.Parse(json).RootElement;

        // Act
        var payload = PayloadRegistry.Deserialize(kind, element);

        // Assert
        Assert.IsNotNull(payload, "a payload is read");
        Assert.AreEqual(kind, payload.Kind, "the kind is the one of the record");
        Assert.IsNull(payload.Validate(), "the payload is valid");
        Assert.IsInstanceOfType(payload, PayloadRegistry.TypeOf(kind), "payload type of the kind");
    }

    #endregion // Methods
}