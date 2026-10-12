using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="PayloadJson"/>
/// </summary>
[TestClass]
public class PayloadJsonTests
{
    #region Constants

    private const string Tricky = "<>&'+ä\u202E😀 lone \uD800 end";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The bytes equal the output of the serializer for texts that the encoder escapes in different ways.
    /// </summary>
    /// <param name="payloadName">The payload to serialize</param>
    [TestMethod]
    [DataRow("gap")]
    [DataRow("service")]
    [DataRow("process")]
    public void PayloadJsonSerializeMatchesTheSerializerOutput(string payloadName)
    {
        // Arrange
        var payload = Payload(payloadName);
        var expected = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), PayloadRegistry.Options);

        // Act
        var actual = PayloadJson.Serialize(payload, int.MaxValue);

        // Assert
        Assert.IsNotNull(actual, "the payload fits");
        Assert.AreSequenceEqual(expected, actual, "bytes of the serializer");
    }

    /// <summary>
    /// JSON of exactly the limit is returned.
    /// </summary>
    [TestMethod]
    public void PayloadJsonSerializeAcceptsJsonOfExactlyTheLimit()
    {
        // Arrange
        var payload = Payload("process");
        var expected = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), PayloadRegistry.Options);

        // Act
        var actual = PayloadJson.Serialize(payload, expected.Length);

        // Assert
        Assert.IsNotNull(actual, "the payload fits");
        Assert.AreSequenceEqual(expected, actual, "bytes of the serializer");
    }

    /// <summary>
    /// JSON one byte above the limit gives <c>null</c>.
    /// </summary>
    [TestMethod]
    public void PayloadJsonSerializeReturnsNullAboveTheLimit()
    {
        // Arrange
        var payload = Payload("process");
        var length = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), PayloadRegistry.Options).Length;

        // Act
        var actual = PayloadJson.Serialize(payload, length - 1);

        // Assert
        Assert.IsNull(actual, "one byte above the limit");
    }

    /// <summary>
    /// <see cref="PayloadJson.Fits"/> is true at the length of the JSON and false one byte below.
    /// </summary>
    [TestMethod]
    public void PayloadJsonFitsTellsTheLimitApart()
    {
        // Arrange
        var payload = Payload("process");
        var length = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), PayloadRegistry.Options).Length;

        // Act
        var atLimit = PayloadJson.Fits(payload, length);
        var below = PayloadJson.Fits(payload, length - 1);

        // Assert
        Assert.IsTrue(atLimit, "exactly the limit fits");
        Assert.IsFalse(below, "one byte above the limit does not fit");
    }

    /// <summary>
    /// Above the limit the allocation follows the limit, not the payload.
    /// </summary>
    /// <param name="method">The method to measure</param>
    [TestMethod]
    [DataRow("Serialize")]
    [DataRow("Fits")]
    public void PayloadJsonAboveTheLimitAllocatesInProportionToTheLimit(string method)
    {
        // Arrange
        var limit = 65536;
        var payload = ProcessesOfEscapedText(300, 4000);
        var length = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), PayloadRegistry.Options).Length;

        Assert.IsGreaterThanOrEqualTo(6000000, length, "the payload is large");
        Run(method, payload, limit);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();

        Run(method, payload, limit);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.IsLessThan(1048576, allocated, "allocated bytes");
    }

    /// <summary>
    /// A payload that just fits allocates at most four times its JSON.
    /// </summary>
    [TestMethod]
    public void PayloadJsonSerializeOfAPayloadThatJustFitsAllocatesAtMostFourTimesItsLength()
    {
        // Arrange
        var payload = ProcessesOfEscapedText(100, 4000);
        var length = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), PayloadRegistry.Options).Length;

        Assert.IsNotNull(PayloadJson.Serialize(payload, length), "warm up");

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = PayloadJson.Serialize(payload, length);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.IsNotNull(actual, "the payload fits");
        Assert.HasCount(length, actual, "length of the JSON");
        Assert.IsLessThanOrEqualTo(4L * length, allocated, "allocated bytes");
    }

    /// <summary>
    /// Runs one method of <see cref="PayloadJson"/>.
    /// </summary>
    /// <param name="method">The method</param>
    /// <param name="payload">The payload</param>
    /// <param name="limit">The limit</param>
    private static void Run(string method, IPayload payload, int limit)
    {
        if (method == "Fits")
        {
            Assert.IsFalse(PayloadJson.Fits(payload, limit), "above the limit");
        }
        else
        {
            Assert.IsNull(PayloadJson.Serialize(payload, limit), "above the limit");
        }
    }

    /// <summary>
    /// Creates a process snapshot whose command lines hold only characters that are escaped with six bytes.
    /// </summary>
    /// <param name="count">The number of processes</param>
    /// <param name="length">The characters of a command line</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot ProcessesOfEscapedText(int count, int length)
    {
        var processes = new List<ProcessSample>();

        for (var pid = 1; pid <= count; pid++)
        {
            processes.Add(new ProcessSample
                          {
                              Pid = pid,
                              Command = "<",
                              Cmdline = new string('<', length)
                          });
        }

        return new ProcessSnapshot
               {
                   Complete = true,
                   Processes = processes
               };
    }

    /// <summary>
    /// Creates a payload with texts that the encoder escapes.
    /// </summary>
    /// <param name="name">The payload name</param>
    /// <returns>The payload</returns>
    private static IPayload Payload(string name)
    {
        return name switch
               {
                   "gap" => new Gap
                            {
                                From = Samples.Base,
                                To = Samples.Base.AddMinutes(1),
                                Cause = Tricky,
                                Collector = Tricky
                            },
                   "service" => new ServiceState
                                {
                                    Unit = Tricky,
                                    LoadState = "loaded",
                                    ActiveState = "active",
                                    SubState = Tricky,
                                    ActiveEnterAt = Samples.Base,
                                    Restarts = 3
                                },
                   _ => new ProcessSnapshot
                        {
                            Complete = true,
                            Total = 2,
                            Processes = [
                                            new ProcessSample
                                            {
                                                Pid = 1,
                                                User = Tricky,
                                                Command = Tricky,
                                                Cmdline = Tricky + Tricky,
                                                State = "S",
                                                CpuPercent = 1.5,
                                                RssBytes = 4096
                                            }
                                        ],
                            Programs = [
                                           new ProgramAggregate
                                           {
                                               Program = Tricky
                                           }
                                       ]
                        }
               };
    }

    #endregion // Methods
}