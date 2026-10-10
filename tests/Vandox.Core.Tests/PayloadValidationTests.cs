using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for the validation of every payload type, driven by JSON documents
/// </summary>
[TestClass]
public class PayloadValidationTests
{
    #region Methods

    /// <summary>
    /// Valid payloads of every kind pass the validation and survive a JSON round trip.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="json">The payload</param>
    [TestMethod]
    [DataRow("metric", """{"name":"cpu.load","value":1.5,"unit":"percent","labels":{"cpu":"0"}}""")]
    [DataRow("log_line", """{"log":"syslog","program":"sshd","pid":12,"priority":3,"message":"hello"}""")]
    [DataRow("log_line", """{"log":"journal","host":"web-1","program":"sshd","pid":12,"priority":3,"message":"hello"}""")]
    [DataRow("gap", """{"from":"2026-10-01T10:00:00Z","to":"2026-10-01T10:05:00Z","cause":"collector_timeout","collector":"proc"}""")]
    [DataRow("gap", """{"from":"2026-10-01T10:00:00Z","to":"2026-10-01T10:05:00Z","cause":"spool_dropped","first_seq":3,"last_seq":9}""")]
    [DataRow("service_state", """{"unit":"mariadb.service","load_state":"loaded","active_state":"active","sub_state":"running","active_enter_at":"2026-10-01T10:00:00Z","restarts":1}""")]
    [DataRow("kernel_event", """{"type":"oom_kill","oom_kill":{"victim_pid":42,"victim_command":"mysqld","anon_rss_bytes":100,"oom_score_adj":-100}}""")]
    [DataRow("kernel_event", """{"type":"boot","boot":{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","previous_boot_id":"0123abcd-0123-0123-0123-0123456789ac","booted_at":"2026-10-01T10:00:00Z","previous_uptime_ns":5}}""")]
    [DataRow("mariadb_status", """{"availability":"up","ping_latency_ns":5,"status":{"Threads_connected":3},"variables":{"max_connections":"151"},"threads":[{"id":1,"user":"root","time_seconds":2,"info":"SELECT 1"}],"complete":true}""")]
    [DataRow("mariadb_status", """{"availability":"down","complete":true}""")]
    [DataRow("process_snapshot", """{"complete":true,"total":2,"processes":[{"pid":1,"ppid":0,"command":"init","state":"S","cpu_percent":0.5,"rss_bytes":10,"oom_score_adj":0}],"programs":[{"program":"init","count":1,"cpu_percent":0.5,"rss_bytes":10}]}""")]
    [DataRow("connection_snapshot", """{"complete":true,"states":[{"proto":"tcp","state":"LISTEN","count":1},{"proto":"udp","count":2}],"processes":[{"pid":3,"command":"sshd","count":1}],"remotes":[{"addr":"10.0.0.2","count":1}],"listeners":[{"proto":"tcp6","local":"[::]:22","pid":3,"command":"sshd"}],"connections":[{"proto":"tcp","local":"10.0.0.1:22","remote":"10.0.0.2:5000","state":"ESTABLISHED"}]}""")]
    public void PayloadValidateValidPayloadReturnsNull(string kind, string json)
    {
        // Arrange
        var payload = Parse(kind, json);

        // Act
        var error = payload.Validate();
        var again = PayloadRegistry.Deserialize(kind, JsonDocument.Parse(PayloadRegistry.Serialize(payload)).RootElement);

        // Assert
        Assert.IsNull(error, "valid payload");
        Assert.AreEqual(kind, payload.Kind, "kind of the payload");
        Assert.IsNull(again!.Validate(), "payload is still valid after a round trip");
    }

    /// <summary>
    /// Invalid payloads name the field and the reason.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="json">The payload</param>
    /// <param name="field">The expected field</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("metric", """{"name":"","value":1}""", "name", "required")]
    [DataRow("metric", """{"name":"-x","value":1}""", "name", "invalid characters")]
    [DataRow("metric", """{"name":"x\n","value":1}""", "name", "invalid characters")]
    [DataRow("metric", """{"name":"x","value":1,"unit":"a b"}""", "unit", "invalid characters")]
    [DataRow("metric", """{"name":"x","value":1,"labels":{"a b":"1"}}""", "labels[\"a b\"]", "invalid characters")]
    [DataRow("log_line", """{"message":"x"}""", "log", "required")]
    [DataRow("log_line", """{"log":"x","pid":-1,"message":"x"}""", "pid", "must not be negative")]
    [DataRow("log_line", """{"log":"x","priority":8,"message":"x"}""", "priority", "must be at most 7")]
    [DataRow("gap", """{"to":"2026-10-01T10:05:00Z","cause":"unknown"}""", "from", "required")]
    [DataRow("gap", """{"from":"2026-10-01T10:00:00+02:00","to":"2026-10-01T10:05:00Z","cause":"unknown"}""", "from", "must be UTC")]
    [DataRow("gap", """{"from":"2026-10-01T10:05:00Z","to":"2026-10-01T10:05:00Z","cause":"unknown"}""", "to", "must be after from")]
    [DataRow("gap", """{"from":"2026-10-01T10:00:00Z","to":"2026-10-01T10:05:00Z","cause":"x"}""", "cause", "unknown value")]
    [DataRow("gap", """{"from":"2026-10-01T10:00:00Z","to":"2026-10-01T10:05:00Z","cause":"collector_timeout"}""", "collector", "required for cause collector_timeout")]
    [DataRow("gap", """{"from":"2026-10-01T10:00:00Z","to":"2026-10-01T10:05:00Z","cause":"unknown","first_seq":9,"last_seq":3}""", "last_seq", "first_seq and last_seq must both be set, first_seq not after last_seq")]
    [DataRow("gap", """{"from":"2026-10-01T10:00:00Z","to":"2026-10-01T10:05:00Z","cause":"spool_dropped"}""", "first_seq", "required for this cause")]
    [DataRow("service_state", """{"unit":"a b","load_state":"loaded","active_state":"active"}""", "unit", "invalid characters")]
    [DataRow("service_state", """{"unit":"a","load_state":"x","active_state":"active"}""", "load_state", "unknown value")]
    [DataRow("service_state", """{"unit":"a","load_state":"loaded","active_state":"x"}""", "active_state", "unknown value")]
    [DataRow("service_state", """{"unit":"a","load_state":"loaded","active_state":"active","sub_state":"A"}""", "sub_state", "invalid characters")]
    [DataRow("service_state", """{"unit":"a","load_state":"loaded","active_state":"active","active_enter_at":"2026-10-01T10:00:00+01:00"}""", "active_enter_at", "must be UTC")]
    [DataRow("kernel_event", """{"type":"x"}""", "type", "unknown value")]
    [DataRow("kernel_event", """{"type":"oom_kill"}""", "oom_kill", "required for type oom_kill")]
    [DataRow("kernel_event", """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"x"},"boot":{"boot_id":"0123abcd-0123-0123-0123-0123456789ab"}}""", "boot", "not allowed for type oom_kill")]
    [DataRow("kernel_event", """{"type":"oom_kill","oom_kill":{"victim_pid":0,"victim_command":"x"}}""", "oom_kill.victim_pid", "must be greater than 0")]
    [DataRow("kernel_event", """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":""}}""", "oom_kill.victim_command", "required")]
    [DataRow("kernel_event", """{"type":"oom_kill","oom_kill":{"victim_pid":1,"victim_command":"x","oom_score_adj":1001}}""", "oom_kill.oom_score_adj", "out of range")]
    [DataRow("kernel_event", """{"type":"boot"}""", "boot", "required for type boot")]
    [DataRow("kernel_event", """{"type":"boot","boot":{"boot_id":"x"},"oom_kill":{"victim_pid":1,"victim_command":"x"}}""", "oom_kill", "not allowed for type boot")]
    [DataRow("kernel_event", """{"type":"boot","boot":{"boot_id":"X"}}""", "boot.boot_id", "invalid characters")]
    [DataRow("kernel_event", """{"type":"boot","boot":{"boot_id":"0123abcd-0123-0123-0123-0123456789ab","previous_uptime_ns":-1}}""", "boot.previous_uptime_ns", "must not be negative")]
    [DataRow("mariadb_status", """{"availability":"x"}""", "availability", "unknown value")]
    [DataRow("mariadb_status", """{"availability":"up","ping_latency_ns":-1}""", "ping_latency_ns", "must not be negative")]
    [DataRow("mariadb_status", """{"availability":"down","status":{"A":1}}""", "availability", "status, variables and threads must be empty unless up")]
    [DataRow("mariadb_status", """{"availability":"up","status":{"1a":1}}""", "status[\"1a\"]", "invalid characters")]
    [DataRow("mariadb_status", """{"availability":"up","variables":{"a-b":"1"}}""", "variables[\"a-b\"]", "invalid characters")]
    [DataRow("mariadb_status", """{"availability":"up","threads":[{"id":1,"time_seconds":1,"user":"u"},{"id":2,"time_seconds":1,"info":"x"},{"id":3,"time_seconds":1,"state":"ok"}]}""", "", "")]
    [DataRow("process_snapshot", """{"complete":true}""", "processes", "at least one process or program required")]
    [DataRow("process_snapshot", """{"processes":[{"pid":0,"command":"x"}]}""", "processes[0].pid", "must be greater than 0")]
    [DataRow("process_snapshot", """{"processes":[{"pid":1,"ppid":-1,"command":"x"}]}""", "processes[0].ppid", "must not be negative")]
    [DataRow("process_snapshot", """{"processes":[{"pid":1,"command":""}]}""", "processes[0].command", "required")]
    [DataRow("process_snapshot", """{"processes":[{"pid":1,"command":"x","state":"SS"}]}""", "processes[0].state", "must be one ASCII letter")]
    [DataRow("process_snapshot", """{"processes":[{"pid":1,"command":"x","state":"1"}]}""", "processes[0].state", "must be one ASCII letter")]
    [DataRow("process_snapshot", """{"processes":[{"pid":1,"command":"x","cpu_percent":-1}]}""", "processes[0].cpu_percent", "must not be negative")]
    [DataRow("process_snapshot", """{"processes":[{"pid":1,"command":"x"},{"pid":1,"command":"y"}]}""", "processes[1].pid", "duplicate pid")]
    [DataRow("process_snapshot", """{"programs":[{"program":"","count":1}]}""", "programs[0].program", "required")]
    [DataRow("process_snapshot", """{"programs":[{"program":"x","count":0}]}""", "programs[0].count", "must be at least 1")]
    [DataRow("process_snapshot", """{"programs":[{"program":"x","count":1,"cpu_percent":-1}]}""", "programs[0].cpu_percent", "must not be negative")]
    [DataRow("process_snapshot", """{"programs":[{"program":"x","count":1},{"program":"x","count":1}]}""", "programs[1].program", "duplicate program")]
    [DataRow("connection_snapshot", """{"states":[{"proto":"icmp","count":1}]}""", "states[0].proto", "unknown value")]
    [DataRow("connection_snapshot", """{"states":[{"proto":"tcp","count":1}]}""", "states[0].state", "required")]
    [DataRow("connection_snapshot", """{"states":[{"proto":"tcp","state":"X","count":1}]}""", "states[0].state", "unknown value")]
    [DataRow("connection_snapshot", """{"states":[{"proto":"udp","count":0}]}""", "states[0].count", "must be at least 1")]
    [DataRow("connection_snapshot", """{"processes":[{"pid":0,"command":"x","count":1}]}""", "processes[0].pid", "must be greater than 0")]
    [DataRow("connection_snapshot", """{"processes":[{"pid":1,"command":"","count":1}]}""", "processes[0].command", "required")]
    [DataRow("connection_snapshot", """{"remotes":[{"count":1}]}""", "remotes[0].addr", "invalid address")]
    [DataRow("connection_snapshot", """{"remotes":[{"addr":"fe80::1%1","count":1}]}""", "remotes[0].addr", "zone not allowed")]
    [DataRow("connection_snapshot", """{"remotes":[{"addr":"2001:db8::1%nosuch","count":1}]}""", "remotes[0].addr", "zone not allowed")]
    [DataRow("connection_snapshot", """{"listeners":[{"proto":"tcp"}]}""", "listeners[0].local", "invalid address")]
    [DataRow("connection_snapshot", """{"listeners":[{"proto":"tcp","local":"1.2.3.4:1","pid":-1}]}""", "listeners[0].pid", "must not be negative")]
    [DataRow("connection_snapshot", """{"connections":[{"proto":"udp","local":"1.2.3.4:1"}]}""", "connections[0].remote", "invalid address")]
    public void PayloadValidateInvalidPayloadNamesFieldAndReason(string kind, string json, string field, string reason)
    {
        // Arrange
        var payload = Parse(kind, json);

        // Act
        var error = payload.Validate();

        // Assert
        if (field.Length == 0)
        {
            Assert.IsNull(error, "payload is valid");
        }
        else
        {
            Assert.IsNotNull(error, "payload is invalid");
            Assert.AreEqual(field, error.Field, "field");
            Assert.AreEqual(reason, error.Reason, "reason");
        }
    }

    /// <summary>
    /// Limits on counts and lengths are enforced.
    /// </summary>
    [TestMethod]
    public void PayloadValidateEnforcesLimits()
    {
        // Arrange
        var tooLong = new string('a', ModelLimits.MaxShortTextBytes + 1);
        var labels = Enumerable.Range(0, ModelLimits.MaxLabels + 1).ToDictionary(index => $"k{index}", _ => "v");
        var processes = Enumerable.Range(1, ModelLimits.MaxItems + 1).Select(pid => Sample(pid)).ToList();
        var status = Enumerable.Range(0, ModelLimits.MaxItems + 1).ToDictionary(index => $"S{index}", _ => 1UL);
        var longLog = new LogLine();
        var longHost = new LogLine();
        var longMessage = new LogLine();
        var manyLabels = new MetricPoint();
        var longLabel = new MetricPoint();
        var longName = new MetricPoint();
        var nanValue = new MetricPoint();
        var manyProcesses = new ProcessSnapshot();
        var manyStatus = new MariaDbStatus();

        longLog.Log = tooLong;
        longHost.Log = "x";
        longHost.Host = tooLong;
        longMessage.Log = "x";
        longMessage.Message = new string('a', ModelLimits.MaxTextBytes + 1);
        manyLabels.Name = "x";
        manyLabels.Labels = labels;
        longLabel.Name = "x";
        longLabel.Labels = [];
        longLabel.Labels["a"] = tooLong;
        longName.Name = new string('a', ModelLimits.MaxNameBytes + 1);
        nanValue.Name = "x";
        nanValue.Value = double.NaN;
        manyProcesses.Processes = processes;
        manyStatus.Availability = MariaDbStatus.Up;
        manyStatus.Status = status;

        // Act
        var logError = longLog.Validate();
        var hostError = longHost.Validate();
        var textError = longMessage.Validate();
        var labelError = manyLabels.Validate();
        var labelValueError = longLabel.Validate();
        var processError = manyProcesses.Validate();
        var statusError = manyStatus.Validate();
        var nameError = longName.Validate();
        var nanError = nanValue.Validate();

        // Assert
        Assert.AreEqual("host", hostError?.Field, "host too long");
        Assert.AreEqual("too long", hostError?.Reason, "host reason");
        Assert.AreEqual("log", logError?.Field, "log too long");
        Assert.AreEqual("too long", logError?.Reason, "log reason");
        Assert.AreEqual("message", textError?.Field, "message too long");
        Assert.AreEqual("labels", labelError?.Field, "too many labels");
        Assert.AreEqual("labels[\"a\"]", labelValueError?.Field, "label value too long");
        Assert.AreEqual("processes", processError?.Field, "too many processes");
        Assert.AreEqual("status", statusError?.Field, "too many status entries");
        Assert.AreEqual("too long", nameError?.Reason, "name too long");
        Assert.AreEqual("must be finite", nanError?.Reason, "NaN is refused");
    }

    /// <summary>
    /// The host of a log line is written with the line and survives a round trip.
    /// </summary>
    [TestMethod]
    public void PayloadLogLineHostSurvivesARoundTrip()
    {
        // Arrange
        var line = new LogLine
                   {
                       Log = "journal",
                       Host = "web-1",
                       Message = "x"
                   };

        // Act
        var json = PayloadRegistry.Serialize(line);
        var again = (LogLine)PayloadRegistry.Deserialize(RecordKind.LogLine, JsonDocument.Parse(json).RootElement)!;

        // Assert
        Assert.Contains("\"host\":\"web-1\"", json, "host is written when set");
        Assert.AreEqual("web-1", again.Host, "host after a round trip");
        Assert.IsNull(again.Validate(), "the line is still valid");
    }

    /// <summary>
    /// A name must not end in a newline, which a multi-line anchor would let through.
    /// </summary>
    [TestMethod]
    public void PayloadValidateNameWithTrailingNewlineIsRefused()
    {
        // Arrange
        var metric = new MetricPoint
                     {
                         Name = "cpu\n",
                         Value = 1
                     };
        var kernel = new KernelEvent
                     {
                         Type = KernelEvent.TypeBoot,
                         Boot = new Boot
                                {
                                    BootId = "0123abcd-0123-0123-0123-0123456789a\n"
                                }
                     };

        // Act
        var metricError = metric.Validate();
        var uuidError = kernel.Validate();

        // Assert
        Assert.AreEqual("invalid characters", metricError?.Reason, "name with a trailing newline");
        Assert.AreEqual("invalid characters", uuidError?.Reason, "UUID with a trailing newline");
    }

    /// <summary>
    /// Creates a process sample.
    /// </summary>
    /// <param name="pid">The process ID</param>
    /// <returns>The sample</returns>
    private static ProcessSample Sample(int pid)
    {
        var sample = new ProcessSample
                     {
                         Pid = pid,
                         Command = "x"
                     };

        return sample;
    }

    /// <summary>
    /// Reads a payload of the kind from JSON.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="json">The JSON text</param>
    /// <returns>The payload</returns>
    private static IPayload Parse(string kind, string json)
    {
        using var document = JsonDocument.Parse(json);

        return PayloadRegistry.Deserialize(kind, document.RootElement)!;
    }

    #endregion // Methods
}