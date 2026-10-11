using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SignatureDetector"/>
/// </summary>
[TestClass]
public class SignatureDetectorTests
{
    #region Constants

    private const string OutOfOrder = "signature detection: lines must be added in capture order";
    private const string AlreadyComplete = "signature detection: already complete";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// An event lists the IDs of its lines in input order, their number, the signature and the capture time of its first line.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorEventListsTheRecordIdsOfItsLines()
    {
        // Arrange
        var catalog = EventCatalog();
        (long, DataRecord)[] records = [(10, Event(0)), (11, Event(5)), (12, Event(10))];

        // Act
        var events = SignatureSamples.Run(catalog, records);

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual("sig", events[0].SignatureId, "signature");
        Assert.AreSequenceEqual([10, 11, 12], events[0].RecordIds.ToList(), "record IDs in input order");
        Assert.AreEqual(3L, events[0].LineCount, "line count");
        Assert.AreEqual(SignatureSamples.Base, events[0].CapturedAt, "time of the first line");
    }

    /// <summary>
    /// A line exactly 60 seconds after the first line of an event still belongs to it.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorMergesCopiesWithinTheWindow()
    {
        // Act
        var events = SignatureSamples.Run(EventCatalog(), Event(0), Event(60));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual(2L, events[0].LineCount, "two lines");
    }

    /// <summary>
    /// A line one tick after the 60 seconds opens a second event.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorSeparatesLinesAfterTheWindow()
    {
        // Act
        var events = SignatureSamples.Run(EventCatalog(), Event(0), SignatureSamples.Line("m", eventName: "e.one", capturedAt: SignatureSamples.Base.AddSeconds(60).AddTicks(1)));

        // Assert
        Assert.HasCount(2, events, "two events");
        Assert.AreSequenceEqual([1], events[0].RecordIds.ToList(), "IDs of the first event");
        Assert.AreSequenceEqual([2], events[1].RecordIds.ToList(), "IDs of the second event");
    }

    /// <summary>
    /// Lines near the end of time merge and the window check does not overflow.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorMergesNearTheLatestTime()
    {
        // Arrange
        var first = SignatureSamples.Line("m", eventName: "e.one", capturedAt: DateTimeOffset.MaxValue.AddSeconds(-30));
        var second = SignatureSamples.Line("m", eventName: "e.one", capturedAt: DateTimeOffset.MaxValue);

        // Act
        var events = SignatureSamples.Run(EventCatalog(), first, second);

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual(2L, events[0].LineCount, "two lines");
    }

    /// <summary>
    /// Lines of one signature with different field values are different events.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorSeparatesDifferentFields()
    {
        // Act
        var events = SignatureSamples.Run(TextCatalog(), Text("a;"), Text("b;"), Text("a;"));

        // Assert
        Assert.HasCount(2, events, "two events");
        Assert.AreEqual("a", events[0].Texts["v"], "first value");
        Assert.AreSequenceEqual([1, 3], events[0].RecordIds.ToList(), "the copy of a joins the first event");
        Assert.AreEqual("b", events[1].Texts["v"], "second value");
    }

    /// <summary>
    /// A line that two signatures match opens an event of each, and the next such line joins each of them, never the other one.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorNeverMergesDifferentSignatures()
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:", "  - id: one", "    description: d", "    event: [e.one]", "  - id: two", "    description: d", "    event: [e.one]");

        // Act
        var events = SignatureSamples.Run(catalog, Event(0), Event(1));

        // Assert
        Assert.AreSequenceEqual(["one", "two"], events.Select(detected => detected.SignatureId).ToList(), "one event per signature");
        Assert.IsTrue(events.All(detected => detected.LineCount == 2), "each event has both lines");
    }

    /// <summary>
    /// Lines of different non-empty hosts are different events.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorSeparatesDifferentHosts()
    {
        // Act
        var events = SignatureSamples.Run(EventCatalog(), Event(0, "h1"), Event(1, "h2"), Event(2, "h1"));

        // Assert
        Assert.HasCount(2, events, "two events");
        Assert.AreEqual("h1", events[0].Host, "host of the first");
        Assert.AreSequenceEqual([1, 3], events[0].RecordIds.ToList(), "the second line of h1 joins the first event");
        Assert.AreEqual("h2", events[1].Host, "host of the second");
    }

    /// <summary>
    /// Field values are compared one by one: two text fields whose joined text is equal are still different.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorComparesFieldsOneByOne()
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:", "  - id: pair", "    description: d", "    message: '^(?<a>[^;]*);(?<b>[^;]*);'", "    fields:", "      a: text", "      b: text");

        // Act
        var events = SignatureSamples.Run(catalog, Text("a|b;c;"), Text("a;b|c;"));

        // Assert
        Assert.HasCount(2, events, "two events");
        Assert.AreEqual("a|b", events[0].Texts["a"], "first field of the first event");
        Assert.AreEqual("a", events[1].Texts["a"], "first field of the second event");
    }

    /// <summary>
    /// An empty host merges with any host, and the event takes the first non-empty host of its lines.
    /// </summary>
    /// <param name="first">The host of the first line</param>
    /// <param name="second">The host of the second line</param>
    /// <param name="expected">The host of the event</param>
    [TestMethod]
    [DataRow("", "h1", "h1")]
    [DataRow("h1", "", "h1")]
    [DataRow("", "", "")]
    public void SignatureDetectorMergesAnEmptyHostWithAnyHost(string first, string second, string expected)
    {
        // Act
        var events = SignatureSamples.Run(EventCatalog(), Event(0, first), Event(1, second));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual(expected, events[0].Host, "host of the event");
        Assert.AreEqual(2L, events[0].LineCount, "two lines");
    }

    /// <summary>
    /// A field that is absent on one line and present on the other is a difference.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorSeparatesAbsentFromPresentFields()
    {
        // Act
        var events = SignatureSamples.Run(TextCatalog(), Text(";"), Text("a;"));

        // Assert
        Assert.HasCount(2, events, "two events");
        Assert.IsEmpty(events[0].Texts, "no field on the first");
        Assert.AreEqual("a", events[1].Texts["v"], "the field on the second");
    }

    /// <summary>
    /// A line gives an event only when the program, the event and the message all hold.
    /// </summary>
    /// <param name="program">The program of the line</param>
    /// <param name="eventName">The event of the line</param>
    /// <param name="message">The message of the line</param>
    /// <param name="expected">The expected number of events</param>
    [TestMethod]
    [DataRow("prog", "e.one", "ok", 1)]
    [DataRow("other", "e.one", "ok", 0)]
    [DataRow("prog", "e.other", "ok", 0)]
    [DataRow("prog", "e.one", "no", 0)]
    public void SignatureDetectorRequiresEveryCondition(string program, string eventName, string message, int expected)
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:", "  - id: sig", "    description: d", "    program: [prog]", "    event: [e.one]", "    message: '^ok'");

        // Act
        var events = SignatureSamples.Run(catalog, SignatureSamples.Line(message, program: program, eventName: eventName));

        // Assert
        Assert.HasCount(expected, events, "events");
    }

    /// <summary>
    /// Any listed program and any listed event is enough; the comparison is ordinal and case-sensitive.
    /// </summary>
    /// <param name="program">The program of the line</param>
    /// <param name="eventName">The event of the line</param>
    /// <param name="expected">The expected number of events</param>
    [TestMethod]
    [DataRow("a", "e.one", 1)]
    [DataRow("b", "e.two", 1)]
    [DataRow("c", "e.one", 0)]
    [DataRow("a", "e.three", 0)]
    [DataRow("A", "e.one", 0)]
    [DataRow("a", "E.one", 0)]
    public void SignatureDetectorMatchesAnyListedProgramOrEvent(string program, string eventName, int expected)
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:", "  - id: sig", "    description: d", "    program: [a, b]", "    event: [e.one, e.two]");

        // Act
        var events = SignatureSamples.Run(catalog, SignatureSamples.Line("m", program: program, eventName: eventName));

        // Assert
        Assert.HasCount(expected, events, "events");
    }

    /// <summary>
    /// A listed program or a listed source of the record is enough for the origin condition, either one; a signature that names only a source takes any program.
    /// </summary>
    /// <param name="withProgram">Whether the signature lists a program as well as a source</param>
    /// <param name="program">The program of the line</param>
    /// <param name="source">The source of the record</param>
    /// <param name="expected">The expected number of events</param>
    [TestMethod]
    [DataRow(true, "p", "other", 1)]
    [DataRow(true, "other", "s", 1)]
    [DataRow(true, "other", "other", 0)]
    [DataRow(true, "P", "S", 0)]
    [DataRow(false, "anything", "s", 1)]
    [DataRow(false, "s", "other", 0)]
    public void SignatureDetectorMatchesAListedProgramOrAListedSource(bool withProgram, string program, string source, int expected)
    {
        // Arrange
        var catalog = withProgram
                          ? SignatureSamples.Catalog("signatures:", "  - id: sig", "    description: d", "    program: [p]", "    source: [s]", "    event: [e.one]")
                          : SignatureSamples.Catalog("signatures:", "  - id: sig", "    description: d", "    source: [s]", "    event: [e.one]");

        // Act
        var events = SignatureSamples.Run(catalog, SignatureSamples.Line("m", source, program, eventName: "e.one"));

        // Assert
        Assert.HasCount(expected, events, "events");
    }

    /// <summary>
    /// Records that hold no log line are ignored, also by the check of the capture order.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorIgnoresRecordsThatAreNoLogLines()
    {
        // Arrange
        var other = new DataRecord
                    {
                        Origin = RecordOrigin.Import,
                        Source = "syslog",
                        CapturedAt = SignatureSamples.Base,
                        Data = new Gap()
                    };
        var empty = new DataRecord
                    {
                        Origin = RecordOrigin.Import,
                        Source = "syslog",
                        CapturedAt = SignatureSamples.Base,
                        Data = null
                    };

        // Act
        var events = SignatureSamples.Run(EventCatalog(), Event(10), other, empty, Event(11));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreSequenceEqual([1, 4], events[0].RecordIds.ToList(), "only the log lines");
    }

    /// <summary>
    /// An event is emitted when a later line is more than 60 seconds after its first line, oldest first; the rest are emitted on completion in opening order.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorEmitsEventsWhenTheirWindowCloses()
    {
        // Arrange
        var detector = new SignatureDetector(TextCatalog());
        var closed = new List<DetectedEvent>();

        // Act
        detector.Add(1, Text("a;", 0), closed);
        detector.Add(2, Text("b;", 30), closed);

        var beforeTheWindow = closed.Count;

        detector.Add(3, Text("c;", 100), closed);

        var afterTheWindow = closed.Select(detected => detected.Texts["v"]).ToList();

        detector.Complete(closed);

        // Assert
        Assert.AreEqual(0, beforeTheWindow, "nothing is emitted inside the window");
        Assert.AreSequenceEqual(["a", "b"], afterTheWindow, "the two events whose window passed, oldest first");
        Assert.AreSequenceEqual(["a", "b", "c"], closed.Select(detected => detected.Texts["v"]).ToList(), "completion emits the rest");
    }

    /// <summary>
    /// A line exactly 60 seconds after the first line does not close the event.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorKeepsAnEventOpenAtExactlyTheWindow()
    {
        // Arrange
        var detector = new SignatureDetector(TextCatalog());
        var closed = new List<DetectedEvent>();

        // Act
        detector.Add(1, Text("a;", 0), closed);
        detector.Add(2, Text("b;", 60), closed);

        // Assert
        Assert.IsEmpty(closed, "nothing is emitted at exactly 60 seconds");
    }

    /// <summary>
    /// The events of one line come in catalog order, which is the order of the file and not the order of the IDs.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorOrdersEventsOfOneLineByCatalog()
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:", "  - id: zeta", "    description: d", "    event: [e.one]", "  - id: alpha", "    description: d", "    event: [e.one]");

        // Act
        var events = SignatureSamples.Run(catalog, Event(0));

        // Assert
        Assert.AreSequenceEqual(["zeta", "alpha"], events.Select(detected => detected.SignatureId).ToList(), "catalog order");
    }

    /// <summary>
    /// A log line earlier than the previous log line is refused with the fixed text; the same time is fine.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorRefusesLinesOutOfCaptureOrder()
    {
        // Arrange
        var detector = new SignatureDetector(EventCatalog());
        var closed = new List<DetectedEvent>();

        detector.Add(1, Event(10), closed);
        detector.Add(2, Event(10), closed);

        // Act
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => detector.Add(3, Event(9), closed), "a line before the previous one");

        // Assert
        Assert.AreEqual(OutOfOrder, exception.Message, "message");
    }

    /// <summary>
    /// Add and Complete after Complete are refused with the fixed text.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorRefusesUseAfterComplete()
    {
        // Arrange
        var detector = new SignatureDetector(EventCatalog());
        var closed = new List<DetectedEvent>();

        detector.Complete(closed);

        // Act
        var add = Assert.ThrowsExactly<InvalidOperationException>(() => detector.Add(1, Event(0), closed), "Add after Complete");
        var complete = Assert.ThrowsExactly<InvalidOperationException>(() => detector.Complete(closed), "Complete after Complete");

        // Assert
        Assert.AreEqual(AlreadyComplete, add.Message, "message of Add");
        Assert.AreEqual(AlreadyComplete, complete.Message, "message of Complete");
    }

    /// <summary>
    /// A null record or a null collection is refused.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorRefusesNullArguments()
    {
        // Arrange
        var detector = new SignatureDetector(EventCatalog());
        var closed = new List<DetectedEvent>();

        // Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => detector.Add(1, null!, closed), "null record");
        Assert.ThrowsExactly<ArgumentNullException>(() => detector.Add(1, Event(0), null!), "null collection in Add");
        Assert.ThrowsExactly<ArgumentNullException>(() => detector.Complete(null!), "null collection in Complete");
    }

    /// <summary>
    /// Integer fields take an optional minus sign and 1 to 19 ASCII digits that fit a 64-bit integer.
    /// </summary>
    /// <param name="captured">The captured text</param>
    /// <param name="expected">The expected number</param>
    [TestMethod]
    [DataRow("42", 42L)]
    [DataRow("0042", 42L)]
    [DataRow("0", 0L)]
    [DataRow("-0", 0L)]
    [DataRow("-17", -17L)]
    [DataRow("9223372036854775807", 9223372036854775807L)]
    [DataRow("-9223372036854775808", -9223372036854775808L)]
    [DataRow("7400000", 7400000L)]
    [DataRow("9007199254740991", 9007199254740991L)]
    [DataRow("9007199254740992", 9007199254740992L)]
    [DataRow("-1", -1L)]
    public void SignatureDetectorConvertsIntegerFields(string captured, long expected)
    {
        // Act
        var events = SignatureSamples.Run(NumberCatalog("integer"), Text(captured + ";"));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual(expected, events[0].Numbers["v"], "the number");
    }

    /// <summary>
    /// Integer fields that do not convert are absent and the event is still detected.
    /// </summary>
    /// <param name="captured">The captured text</param>
    [TestMethod]
    [DataRow("9223372036854775808")]
    [DataRow("-9223372036854775809")]
    [DataRow("00000000000000000001")]
    [DynamicData(nameof(NotNumbers))]
    public void SignatureDetectorOmitsIntegerFieldsThatDoNotConvert(string captured)
    {
        // Act
        var events = SignatureSamples.Run(NumberCatalog("integer"), Text(captured + ";"));

        // Assert
        Assert.HasCount(1, events, "the event is detected");
        Assert.IsEmpty(events[0].Numbers, "the field is absent");
    }

    /// <summary>
    /// Kibibyte fields take 1 to 19 ASCII digits without a sign and give bytes.
    /// </summary>
    /// <param name="captured">The captured text</param>
    /// <param name="expected">The expected number of bytes</param>
    [TestMethod]
    [DataRow("42", 43008L)]
    [DataRow("0042", 43008L)]
    [DataRow("0", 0L)]
    [DataRow("7400000", 7577600000L)]
    [DataRow("9007199254740991", 9223372036854774784L)]
    public void SignatureDetectorConvertsKibibytesToBytes(string captured, long expected)
    {
        // Act
        var events = SignatureSamples.Run(NumberCatalog("kibibytes"), Text(captured + ";"));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual(expected, events[0].Numbers["v"], "the number of bytes");
    }

    /// <summary>
    /// Kibibyte fields with a sign or too large to give bytes in 64 bits are absent and the event is still detected.
    /// </summary>
    /// <param name="captured">The captured text</param>
    [TestMethod]
    [DataRow("-0")]
    [DataRow("-17")]
    [DataRow("9223372036854775807")]
    [DataRow("-9223372036854775808")]
    [DataRow("9223372036854775808")]
    [DataRow("-9223372036854775809")]
    [DataRow("00000000000000000001")]
    [DataRow("9007199254740992")]
    [DataRow("-1")]
    [DynamicData(nameof(NotNumbers))]
    public void SignatureDetectorOmitsKibibytesThatDoNotConvert(string captured)
    {
        // Act
        var events = SignatureSamples.Run(NumberCatalog("kibibytes"), Text(captured + ";"));

        // Assert
        Assert.HasCount(1, events, "the event is detected");
        Assert.IsEmpty(events[0].Numbers, "the field is absent");
    }

    /// <summary>
    /// A minus sign or a digit of another script is no part of a number.
    /// </summary>
    /// <param name="type">The field type</param>
    /// <param name="captured">The captured text, with a character above U+007F</param>
    [TestMethod]
    [DataRow("integer", "\u22125")]
    [DataRow("integer", "\uFF15")]
    [DataRow("integer", "\u0664\u0662")]
    [DataRow("kibibytes", "\u22125")]
    [DataRow("kibibytes", "\uFF15")]
    [DataRow("kibibytes", "\u0664\u0662")]
    public void SignatureDetectorOmitsNonAsciiDigitsAndSigns(string type, string captured)
    {
        // Arrange
        Assert.Contains(character => character > 127, captured, "the input holds a character above U+007F, so the escape was not lost");

        // Act
        var events = SignatureSamples.Run(NumberCatalog(type), Text(captured + ";"));

        // Assert
        Assert.HasCount(1, events, "the event is detected");
        Assert.IsEmpty(events[0].Numbers, "the field is absent");
    }

    /// <summary>
    /// A field whose group did not take part or matched the empty string is absent, and the event is still detected.
    /// </summary>
    /// <param name="pattern">The message pattern with the group v</param>
    /// <param name="message">The message</param>
    [TestMethod]
    [DataRow("^(?<v>[^;]*);", ";")]
    [DataRow("^(?:x(?<v>[a-z]+))?y", "y")]
    public void SignatureDetectorOmitsFieldsWithoutText(string pattern, string message)
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:", "  - id: sig", "    description: d", $"    message: '{pattern}'", "    fields:", "      v: text");

        // Act
        var events = SignatureSamples.Run(catalog, Text(message));

        // Assert
        Assert.HasCount(1, events, "the event is detected");
        Assert.IsEmpty(events[0].Texts, "the field is absent");
    }

    /// <summary>
    /// A text field is cut to 1,024 UTF-8 bytes at a character boundary.
    /// </summary>
    /// <param name="ascii">The number of ASCII characters</param>
    /// <param name="accented">The number of two-byte characters after them</param>
    /// <param name="expectedLength">The expected length of the field in characters</param>
    [TestMethod]
    [DataRow(0, 2000, 512)]
    [DataRow(1023, 1, 1023)]
    [DataRow(1022, 1, 1023)]
    [DataRow(1024, 0, 1024)]
    [DataRow(1025, 0, 1024)]
    [DataRow(10, 0, 10)]
    public void SignatureDetectorCutsTextFieldsAt1024Bytes(int ascii, int accented, int expectedLength)
    {
        // Arrange
        var captured = new string('a', ascii) + new string((char)0xE9, accented);

        // Act
        var events = SignatureSamples.Run(TextCatalog(), Text(captured + ";"));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual(captured[..expectedLength], events[0].Texts["v"], "the field is the text cut at a character boundary");
    }

    /// <summary>
    /// An emitted event keeps its lists: later lines change nothing in it, and its lists cannot be cast to mutable ones.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorEmitsEventsItNoLongerChanges()
    {
        // Arrange
        var detector = new SignatureDetector(EventCatalog());
        var closed = new List<DetectedEvent>();

        detector.Add(1, Event(0), closed);
        detector.Add(2, Event(1), closed);
        detector.Add(3, Event(100), closed);

        var emitted = closed.Single();

        // Act
        for (var index = 4; index < 20; index++)
        {
            detector.Add(index, Event(100), closed);
        }

        detector.Complete(closed);

        // Assert
        Assert.AreSequenceEqual([1, 2], emitted.RecordIds.ToList(), "record IDs of the emitted event");
        Assert.AreEqual(2L, emitted.LineCount, "line count of the emitted event");
        Assert.IsNotInstanceOfType<List<long>>(emitted.RecordIds, "RecordIds is no List");
        Assert.IsNotInstanceOfType<long[]>(emitted.RecordIds, "RecordIds is no array");

        if (emitted.RecordIds is IList<long> mutable)
        {
            Assert.ThrowsExactly<NotSupportedException>(() => mutable.Add(5), "RecordIds refuses an added ID");
        }
    }

    /// <summary>
    /// Adding the 1,025th distinct event inside one window emits the oldest open event.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorClosesTheOldestEventBeyondTheOpenLimit()
    {
        // Arrange
        var detector = new SignatureDetector(TextCatalog());
        var closed = new List<DetectedEvent>();

        for (var index = 1; index <= 1024; index++)
        {
            detector.Add(index, Text($"{index};"), closed);
        }

        var atTheLimit = closed.Count;

        // Act
        detector.Add(1025, Text("1025;"), closed);

        var beyondTheLimit = closed.Select(detected => detected.Texts["v"]).ToList();

        detector.Complete(closed);

        // Assert
        Assert.AreEqual(0, atTheLimit, "1,024 open events close nothing");
        Assert.AreSequenceEqual(["1"], beyondTheLimit, "the first event is emitted by the 1,025th");
        Assert.HasCount(1025, closed, "completion emits the rest");
        Assert.AreEqual("1025", closed[^1].Texts["v"], "the newest event is the last");
    }

    /// <summary>
    /// An event keeps the first 256 record IDs and counts every line.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorCountsLinesBeyondTheIdLimit()
    {
        // Act
        var events = SignatureSamples.Run(EventCatalog(), Enumerable.Range(1, 300).Select(id => ((long)id, Event(0))));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreSequenceEqual(Enumerable.Range(1, 256).Select(id => (long)id).ToList(), events[0].RecordIds.ToList(), "the first 256 IDs");
        Assert.AreEqual(300L, events[0].LineCount, "every line is counted");
    }

    /// <summary>
    /// A flood of long lines with distinct text fields retains only the cut fields of the open events, not the messages, matches or records.
    /// </summary>
    [TestMethod]
    public void SignatureDetectorRetainsBoundedMemoryUnderAFlood()
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:", "  - id: flood", "    description: d", "    message: '^(?<t>[^;]*);'", "    fields:", "      t: text");
        var warmUp = new SignatureDetector(catalog);
        var discarded = new DiscardingEventCollection();

        warmUp.Add(1, Flood(0), discarded);
        warmUp.Complete(discarded);

        var detector = new SignatureDetector(catalog);
        var closed = new DiscardingEventCollection();
        var before = RetainedBytes();

        // Act
        for (var index = 1; index <= 3000; index++)
        {
            detector.Add(index, Flood(index), closed);
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(detector);

        // Assert
        Assert.AreEqual(3000 - 1024, closed.Added, "all but the 1,024 open events were emitted");
        Assert.IsLessThan(8 * 1024 * 1024, retained, "retained bytes after the last line; keeping uncut fields, messages, matches or records would be at least 32 MiB");
    }

    /// <summary>
    /// Gets captured texts that are no number for any numeric field type.
    /// </summary>
    /// <returns>The cases</returns>
    private static IEnumerable<object[]> NotNumbers()
    {
        string[] texts = ["+5", "-+5", "--5", "-", "5 ", " 5", "5\n", "5\0", "1,000", "4.2", "1e3", "0x1F", "(5)"];

        return texts.Select(text => new object[] { text });
    }

    /// <summary>
    /// Returns the bytes the process retains after a full collection, which is how the heap-bound claims are measured.
    /// </summary>
    /// <returns>The number of bytes</returns>
    private static long RetainedBytes()
    {
#pragma warning disable S1215
        return GC.GetTotalMemory(true);
#pragma warning restore S1215
    }

    /// <summary>
    /// Creates the record of one line of the flood: a text capture of 16,000 characters that starts with the line number, then a semicolon and filler up to 16,380 characters.
    /// </summary>
    /// <param name="number">The number of the line</param>
    /// <returns>The record</returns>
    private static DataRecord Flood(int number)
    {
        return SignatureSamples.Line($"{number:D8}{new string('a', 16000 - 8)};{new string('b', 16380 - 16001)}");
    }

    /// <summary>
    /// Creates the catalog of one signature that matches the event <c>e.one</c> and has no fields.
    /// </summary>
    /// <returns>The catalog</returns>
    private static SignatureCatalog EventCatalog()
    {
        return SignatureSamples.Catalog("signatures:", "  - id: sig", "    description: d", "    event: [e.one]");
    }

    /// <summary>
    /// Creates the catalog of one signature with the text field v, the text before the first semicolon.
    /// </summary>
    /// <returns>The catalog</returns>
    private static SignatureCatalog TextCatalog()
    {
        return NumberCatalog("text");
    }

    /// <summary>
    /// Creates the catalog of one signature with the field v of a type, the text before the first semicolon, which may hold any other character.
    /// </summary>
    /// <param name="type">The field type</param>
    /// <returns>The catalog</returns>
    private static SignatureCatalog NumberCatalog(string type)
    {
        return SignatureSamples.Catalog("signatures:", "  - id: sig", "    description: d", "    message: '^(?<v>[^;]*);'", "    fields:", $"      v: {type}");
    }

    /// <summary>
    /// Creates a record of the event <c>e.one</c>.
    /// </summary>
    /// <param name="seconds">The seconds after the base time</param>
    /// <param name="host">The host</param>
    /// <returns>The record</returns>
    private static DataRecord Event(int seconds, string host = "")
    {
        return SignatureSamples.Line("m", host: host, eventName: "e.one", capturedAt: SignatureSamples.Base.AddSeconds(seconds));
    }

    /// <summary>
    /// Creates a record with a message.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="seconds">The seconds after the base time</param>
    /// <returns>The record</returns>
    private static DataRecord Text(string message, int seconds = 0)
    {
        return SignatureSamples.Line(message, capturedAt: SignatureSamples.Base.AddSeconds(seconds));
    }

    #endregion // Methods
}