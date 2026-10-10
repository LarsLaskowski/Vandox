using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Vandox.Core.Model;
using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="NullAsAbsentConverterFactory"/>
/// </summary>
[TestClass]
public class NullAsAbsentConverterFactoryTests
{
    #region Methods

    /// <summary>
    /// The factory claims <c>string</c>, the non-nullable value types of the model and lists of model classes.
    /// </summary>
    /// <param name="type">The type</param>
    [TestMethod]
    [DataRow(typeof(string))]
    [DataRow(typeof(bool))]
    [DataRow(typeof(byte))]
    [DataRow(typeof(short))]
    [DataRow(typeof(int))]
    [DataRow(typeof(uint))]
    [DataRow(typeof(long))]
    [DataRow(typeof(ulong))]
    [DataRow(typeof(double))]
    [DataRow(typeof(DateTimeOffset))]
    [DataRow(typeof(List<ProcessSample>))]
    [DataRow(typeof(List<MariaDbThread>))]
    public void NullAsAbsentConverterFactoryClaimsType(Type type)
    {
        // Arrange
        var factory = new NullAsAbsentConverterFactory();

        // Act
        var result = factory.CanConvert(type);

        // Assert
        Assert.IsTrue(result, $"{type} is claimed");
    }

    /// <summary>
    /// The factory leaves nullable value types, other lists, dictionaries, sub-objects and the types with their own converter alone.
    /// </summary>
    /// <param name="type">The type</param>
    [TestMethod]
    [DataRow(typeof(int?))]
    [DataRow(typeof(DateTimeOffset?))]
    [DataRow(typeof(List<string>))]
    [DataRow(typeof(Dictionary<string, string>))]
    [DataRow(typeof(JsonElement))]
    [DataRow(typeof(IPAddress))]
    [DataRow(typeof(OomKill))]
    public void NullAsAbsentConverterFactoryLeavesTypeAlone(Type type)
    {
        // Arrange
        var factory = new NullAsAbsentConverterFactory();

        // Act
        var result = factory.CanConvert(type);

        // Assert
        Assert.IsFalse(result, $"{type} is not claimed");
    }

    /// <summary>
    /// The factory creates a scalar converter for a scalar type and a list converter for a list type.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterFactoryCreatesConverterForClaimedType()
    {
        // Arrange
        var factory = new NullAsAbsentConverterFactory();
        var options = new JsonSerializerOptions();

        // Act
        var text = factory.CreateConverter(typeof(string), options);
        var number = factory.CreateConverter(typeof(uint), options);
        var date = factory.CreateConverter(typeof(DateTimeOffset), options);
        var list = factory.CreateConverter(typeof(List<ProcessSample>), options);

        // Assert
        Assert.IsInstanceOfType<NullAsAbsentConverter<string>>(text, "string");
        Assert.IsInstanceOfType<NullAsAbsentConverter<uint>>(number, "uint");
        Assert.IsInstanceOfType<NullAsAbsentConverter<DateTimeOffset>>(date, "date");
        Assert.IsInstanceOfType<NullElementListConverter<ProcessSample>>(list, "list");
    }

    /// <summary>
    /// The converters the factory creates read a <c>null</c> as the absent value: an empty string, a zero, the default date.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterFactoryReadsNullAsAbsentValue()
    {
        // Arrange
        var options = new JsonSerializerOptions
                      {
                          Converters = {
                                           new NullAsAbsentConverterFactory()
                                       }
                      };

        // Act
        var text = JsonSerializer.Deserialize<string>("null", options);
        var flag = JsonSerializer.Deserialize<bool>("null", options);
        var small = JsonSerializer.Deserialize<byte>("null", options);
        var number = JsonSerializer.Deserialize<int>("null", options);
        var big = JsonSerializer.Deserialize<ulong>("null", options);
        var real = JsonSerializer.Deserialize<double>("null", options);
        var date = JsonSerializer.Deserialize<DateTimeOffset>("null", options);
        var optional = JsonSerializer.Deserialize<int?>("null", options);

        // Assert
        Assert.AreEqual(string.Empty, text, "string");
        Assert.IsFalse(flag, "bool");
        Assert.AreEqual((byte)0, small, "byte");
        Assert.AreEqual(0, number, "int");
        Assert.AreEqual(0UL, big, "ulong");
        Assert.AreEqual(0.0, real, "double");
        Assert.AreEqual(default, date, "date");
        Assert.IsNull(optional, "a nullable number stays null");
    }

    /// <summary>
    /// A list read through the factory holds no null reference, and a null list stays null.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterFactoryReadsNullListElementAsEmptyElement()
    {
        // Arrange
        var options = new JsonSerializerOptions
                      {
                          Converters = {
                                           new NullAsAbsentConverterFactory()
                                       }
                      };

        // Act
        var list = JsonSerializer.Deserialize<List<ProcessSample>>("""[null,{"pid":4,"command":null}]""", options);
        var none = JsonSerializer.Deserialize<List<ProcessSample>>("null", options);

        // Assert
        Assert.IsNotNull(list, "the list is read");
        Assert.HasCount(2, list, "both elements");
        Assert.AreEqual(0, list[0].Pid, "the null element has pid 0");
        Assert.AreEqual(string.Empty, list[0].Command, "the null element has an empty command");
        Assert.AreEqual(4, list[1].Pid, "the second element is read");
        Assert.AreEqual(string.Empty, list[1].Command, "a null command reads as empty");
        Assert.IsNull(none, "a null list stays null");
    }

    /// <summary>
    /// Writing a payload yields the same JSON as before the factory: labels, a list, nullable numbers, an address and an empty optional string.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterFactoryKeepsWrittenJson()
    {
        // Arrange
        var labels = new[] { ("device", "sda1"), ("mount", string.Empty) }.ToDictionary(pair => pair.Item1, pair => pair.Item2);
        var metric = new MetricPoint
                     {
                         Name = "cpu",
                         Value = 1.5,
                         Unit = string.Empty,
                         Labels = labels
                     };
        var state = new StateCount
                    {
                        Proto = "tcp",
                        State = string.Empty,
                        Count = 3
                    };
        var remote = new RemoteCount
                     {
                         Addr = IPAddress.Parse("2001:db8::1"),
                         Count = 4
                     };
        var listener = new Listener
                       {
                           Proto = "tcp",
                           Local = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 80),
                           Pid = 0,
                           Command = string.Empty
                       };
        var snapshot = new ConnectionSnapshot
                       {
                           Complete = true,
                           States = [state],
                           Remotes = [remote],
                           Listeners = [listener]
                       };
        var sample = new ProcessSample
                     {
                         Pid = 1,
                         Command = "init",
                         PssBytes = 5,
                         OomScoreAdj = -3,
                         StartedAt = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero)
                     };
        var process = new ProcessSnapshot
                      {
                          Complete = true,
                          Total = 7,
                          Processes = [sample]
                      };

        // Act
        var metricJson = PayloadRegistry.Serialize(metric);
        var snapshotJson = PayloadRegistry.Serialize(snapshot);
        var processJson = PayloadRegistry.Serialize(process);

        // Assert
        Assert.AreEqual("""{"name":"cpu","value":1.5,"unit":"","labels":{"device":"sda1","mount":""},"Kind":"metric"}""", metricJson, "metric");
        Assert.AreEqual("""{"complete":true,"states":[{"proto":"tcp","state":"","count":3}],"remotes":[{"addr":"2001:db8::1","count":4}],"listeners":[{"proto":"tcp","local":"10.0.0.1:80","command":""}],"Kind":"connection_snapshot"}""", snapshotJson, "connection snapshot");
        Assert.AreEqual("""{"complete":true,"total":7,"processes":[{"pid":1,"user":"","command":"init","cmdline":"","state":"","started_at":"2026-03-01T08:00:00+00:00","cpu_percent":0,"rss_bytes":0,"pss_bytes":5,"oom_score_adj":-3}],"Kind":"process_snapshot"}""", processJson, "process snapshot");
    }

    /// <summary>
    /// Every JSON-bound property type of the wire header, the envelope and every payload, at any depth, is claimed by the factory or handled otherwise.
    /// </summary>
    [TestMethod]
    public void NullAsAbsentConverterFactoryClaimsEveryPropertyTypeOfTheModel()
    {
        // Arrange
        var factory = new NullAsAbsentConverterFactory();
        var visited = new HashSet<Type>();
        var problems = new List<string>();
        var kinds = new[] { RecordKind.Metric, RecordKind.ProcessSnapshot, RecordKind.ConnectionSnapshot, RecordKind.ServiceState, RecordKind.MariaDbStatus, RecordKind.KernelEvent, RecordKind.LogLine, RecordKind.Gap };
        List<Type> roots = [typeof(WireHeader), typeof(Envelope)];

        roots.AddRange(kinds.Select(kind => PayloadRegistry.TypeOf(kind) ?? throw new InvalidOperationException($"no payload type for {kind}")));

        // Act
        foreach (var root in roots)
        {
            Walk(root, factory, visited, problems);
        }

        // Assert
        Assert.IsEmpty(problems, string.Join("; ", problems));

        foreach (var expected in new[] { typeof(WireHeader), typeof(Envelope), typeof(MetricPoint), typeof(ProcessSample), typeof(ProgramAggregate), typeof(StateCount), typeof(Connection), typeof(MariaDbThread), typeof(OomKill), typeof(Boot), typeof(Gap) })
        {
            Assert.Contains(expected, visited, $"{expected.Name} is walked");
        }
    }

    /// <summary>
    /// Checks the JSON-bound properties of a type and walks the classes reached through them.
    /// </summary>
    /// <param name="type">The class</param>
    /// <param name="factory">The factory</param>
    /// <param name="visited">The classes walked so far</param>
    /// <param name="problems">Receives one entry per property type that is not covered</param>
    private static void Walk(Type type, NullAsAbsentConverterFactory factory, HashSet<Type> visited, List<string> problems)
    {
        if (visited.Add(type))
        {
            WalkProperties(type, factory, visited, problems);
        }
    }

    /// <summary>
    /// Checks the JSON-bound properties of a class and walks the classes reached through them.
    /// </summary>
    /// <param name="type">The class</param>
    /// <param name="factory">The factory</param>
    /// <param name="visited">The classes walked so far</param>
    /// <param name="problems">Receives one entry per property type that is not covered</param>
    private static void WalkProperties(Type type, NullAsAbsentConverterFactory factory, HashSet<Type> visited, List<string> problems)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(IsJsonBound))
        {
            var propertyType = property.PropertyType;

            if (property.GetCustomAttribute<JsonConverterAttribute>() is not null || propertyType == typeof(JsonElement?) || propertyType == typeof(JsonElement))
            {
                continue;
            }

            var next = CheckProperty(propertyType, factory, out var problem);

            if (problem is not null)
            {
                problems.Add($"{type.Name}.{property.Name} ({propertyType}): {problem}");
            }

            if (next is not null)
            {
                Walk(next, factory, visited, problems);
            }
        }
    }

    /// <summary>
    /// Tells whether the serializer reads the property from JSON.
    /// </summary>
    /// <param name="property">The property</param>
    /// <returns><c>true</c> for a public property with a public setter that is not ignored</returns>
    private static bool IsJsonBound(PropertyInfo property)
    {
        return property.SetMethod is { IsPublic: true } && property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always };
    }

    /// <summary>
    /// Checks one property type against the rule of the factory.
    /// </summary>
    /// <param name="type">The property type</param>
    /// <param name="factory">The factory</param>
    /// <param name="problem">Receives the reason when the type is not covered, otherwise <c>null</c></param>
    /// <returns>The class to walk next, or <c>null</c></returns>
    private static Type? CheckProperty(Type type, NullAsAbsentConverterFactory factory, out string? problem)
    {
        problem = null;

        var nullable = Nullable.GetUnderlyingType(type);

        if (nullable is not null)
        {
            problem = factory.CanConvert(nullable) ? null : "the underlying type of a nullable is not claimed";

            return null;
        }

        if (type.IsGenericType)
        {
            return CheckGeneric(type, factory, out problem);
        }

        if (type == typeof(string) || type.IsValueType)
        {
            problem = factory.CanConvert(type) ? null : "not claimed";

            return null;
        }

        if (type is { IsClass: true, IsAbstract: false } && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            return type;
        }

        problem = "neither claimed nor a class to walk";

        return null;
    }

    /// <summary>
    /// Checks a list or dictionary property type.
    /// </summary>
    /// <param name="type">The generic property type</param>
    /// <param name="factory">The factory</param>
    /// <param name="problem">Receives the reason when the type is not covered, otherwise <c>null</c></param>
    /// <returns>The element class to walk next, or <c>null</c></returns>
    private static Type? CheckGeneric(Type type, NullAsAbsentConverterFactory factory, out string? problem)
    {
        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();

        problem = null;

        if (definition == typeof(List<>))
        {
            problem = factory.CanConvert(type) ? null : "the list is not claimed";

            return arguments[0].IsClass && arguments[0] != typeof(string) ? arguments[0] : null;
        }

        if (definition == typeof(Dictionary<,>) && arguments[0] == typeof(string))
        {
            problem = factory.CanConvert(arguments[1]) ? null : "the value type of the dictionary is not claimed";

            return null;
        }

        problem = "generic type that is neither a list nor a dictionary with string keys";

        return null;
    }

    #endregion // Methods
}