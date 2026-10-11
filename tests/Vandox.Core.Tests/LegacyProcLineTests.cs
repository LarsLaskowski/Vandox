using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LegacyProcLine"/>
/// </summary>
[TestClass]
public class LegacyProcLineTests
{
    #region Constants

    private const string SomeLine = "memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=12345";
    private const string FullLine = "full avg10=0.10 avg60=0.05 avg300=0.01 total=999";
    private const double Delta = 1e-9;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A meminfo line gives the field and its value: kilobytes as bytes, a plain count as it is.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="field">The expected field</param>
    /// <param name="value">The expected value</param>
    /// <param name="kilobytes">Whether the line carries kB</param>
    [TestMethod]
    [DataRow("MemTotal:       131926528 kB", "MemTotal", 135092764672UL, true)]
    [DataRow("HugePages_Total:       0", "HugePages_Total", 0UL, false)]
    [DataRow("Active(anon):  1000 kB", "Active(anon)", 1024000UL, true)]
    [DataRow("MemFree: 18014398509481983 kB", "MemFree", 18446744073709550592UL, true)]
    [DataRow("Hugepagesize:       2048 kB", "Hugepagesize", 2097152UL, true)]
    [DataRow("HugePages_Free: 18446744073709551615", "HugePages_Free", 18446744073709551615UL, false)]
    [DataRow("Zero: 0 kB", "Zero", 0UL, true)]
    public void LegacyProcLineTryParseMeminfoReadsKilobytesAndCounts(string line, string field, ulong value, bool kilobytes)
    {
        // Act
        var read = LegacyProcLine.TryParseMeminfo(line, out var actualField, out var actualValue, out var actualKilobytes);

        // Assert
        Assert.IsTrue(read, "the line is read");
        Assert.AreEqual(field, actualField, "the field");
        Assert.AreEqual(value, actualValue, "the value in bytes or as a count");
        Assert.AreEqual(kilobytes, actualKilobytes, "whether the value was in kB");
    }

    /// <summary>
    /// A line that is not a name, a colon, a number and an optional kB is not read; a value that does not fit 64 bits is no value.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("MemTotal")]
    [DataRow("MemTotal: kB")]
    [DataRow("MemTotal: 1 MB")]
    [DataRow("MemTotal: 1 kB trailing")]
    [DataRow("MemTotal: -1 kB")]
    [DataRow("MemTotal: 1.5 kB")]
    [DataRow("MemTotal: 1e3 kB")]
    [DataRow("MemTotal: 123456789012345678901 kB")]
    [DataRow("MemTotal: 18014398509481984 kB")]
    [DataRow("HugePages_Total: 18446744073709551616")]
    [DataRow("HugePages_Total: 123456789012345678901")]
    [DataRow(": 1 kB")]
    [DataRow("Mem Total: 1 kB")]
    [DataRow("Mem-Total: 1 kB")]
    [DataRow("MemTätal: 1 kB")]
    [DataRow("MemTotal: ٣ kB")]
    [DataRow("MemTotal:: 1 kB")]
    public void LegacyProcLineTryParseMeminfoRefusesOtherForms(string line)
    {
        // Act
        var read = LegacyProcLine.TryParseMeminfo(line, out _, out _, out _);

        // Assert
        Assert.IsFalse(read, "the line is not read");
    }

    /// <summary>
    /// A name of 64 characters is read and one of 65 is not.
    /// </summary>
    [TestMethod]
    public void LegacyProcLineTryParseMeminfoBoundsTheNameAt64Characters()
    {
        // Act
        var accepted = LegacyProcLine.TryParseMeminfo(new string('A', 64) + ": 1 kB", out _, out _, out _);
        var refused = LegacyProcLine.TryParseMeminfo(new string('A', 65) + ": 1 kB", out _, out _, out _);

        // Assert
        Assert.IsTrue(accepted, "64 characters");
        Assert.IsFalse(refused, "65 characters");
    }

    /// <summary>
    /// A pressure line gives the some and the full entry in one line, or the full entry as a line of its own after a some entry.
    /// </summary>
    [TestMethod]
    public void LegacyProcLineTryParsePressureReadsOneAndTwoLineForms()
    {
        // Act
        var both = LegacyProcLine.TryParsePressure(SomeLine + " full avg10=0.10 avg60=0.05 avg300=0.01 total=999", null);
        var some = LegacyProcLine.TryParsePressure("cpu: some avg10=1.00 avg60=2.00 avg300=3.00 total=7", null);
        var full = LegacyProcLine.TryParsePressure(FullLine, "io");
        var zero = LegacyProcLine.TryParsePressure("io: some avg10=0 avg60=0 avg300=0 total=0", null);

        // Assert
        Assert.IsNotNull(both, "one-line form");
        Assert.HasCount(2, both, "some and full");
        Assert.AreEqual(new LegacyPressure("memory", "some", 0.5, 0.25, 0.1, 12345), both[0], "some entry");
        Assert.AreEqual(new LegacyPressure("memory", "full", 0.1, 0.05, 0.01, 999), both[1], "full entry");
        Assert.IsNotNull(some, "some only");
        Assert.HasCount(1, some, "one entry");
        Assert.AreEqual(new LegacyPressure("cpu", "some", 1.0, 2.0, 3.0, 7), some[0], "some entry of cpu");
        Assert.IsNotNull(full, "full after a resource line");
        Assert.HasCount(1, full, "one entry");
        Assert.AreEqual("io", full[0].Resource, "the resource of the line before");
        Assert.AreEqual("full", full[0].Kind, "the kind");
        Assert.AreEqual(0.1, full[0].Avg10, Delta, "avg10");
        Assert.AreEqual(999UL, full[0].Total, "total");
        Assert.IsNotNull(zero, "plain integers as averages");
        Assert.HasCount(1, zero, "one entry");
    }

    /// <summary>
    /// The line n/a, with or without a resource, means the pressure is not available.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expected">Whether the line means not available</param>
    [TestMethod]
    [DataRow("n/a", true)]
    [DataRow("memory: n/a", true)]
    [DataRow("cpu: n/a", true)]
    [DataRow("io: n/a", true)]
    [DataRow(SomeLine, false)]
    [DataRow(FullLine, false)]
    [DataRow("", false)]
    public void LegacyProcLineTryParsePressureAcceptsNotAvailable(string line, bool expected)
    {
        // Act
        var actual = LegacyProcLine.IsNotAvailable(line);

        // Assert
        Assert.AreEqual(expected, actual, "whether the line is n/a");
    }

    /// <summary>
    /// A pressure line of another shape, with a number outside the plain form or a total that does not fit 64 bits, is not read.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="previous">The resource of the line before, or <c>null</c></param>
    [TestMethod]
    [DataRow("", null)]
    [DataRow("memory: some avg10=0.50", null)]
    [DataRow("gpu: some avg10=0.50 avg60=0.25 avg300=0.10 total=1", null)]
    [DataRow("memory: some avg10=-0.5 avg60=0.25 avg300=0.10 total=1", null)]
    [DataRow("memory: some avg10=1e3 avg60=0.25 avg300=0.10 total=1", null)]
    [DataRow("memory: some avg10=NaN avg60=0.25 avg300=0.10 total=1", null)]
    [DataRow("memory: some avg10=0,5 avg60=0.25 avg300=0.10 total=1", null)]
    [DataRow("memory: some avg10=0.1234567 avg60=0.25 avg300=0.10 total=1", null)]
    [DataRow("memory: some avg10=1234567890123456.0 avg60=0.25 avg300=0.10 total=1", null)]
    [DataRow("memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=-1", null)]
    [DataRow("memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=1.5", null)]
    [DataRow("memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=123456789012345678901", null)]
    [DataRow("memory: some avg10=0.50 avg60=0.25 avg300=0.10 total=18446744073709551616", null)]
    [DataRow("memory: some avg10=0.50 avg60=0.25 avg300=0.10", null)]
    [DataRow(FullLine, null)]
    [DataRow("full avg10=0.10 avg60=0.05", "memory")]
    public void LegacyProcLineTryParsePressureRefusesOtherForms(string line, string? previous)
    {
        // Act
        var entries = LegacyProcLine.TryParsePressure(line, previous);

        // Assert
        Assert.IsNull(entries, "the line is not read");
    }

    /// <summary>
    /// A vmstat line gives the counter and its value, whatever the counter.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="counter">The expected counter</param>
    /// <param name="value">The expected value</param>
    [TestMethod]
    [DataRow("pswpin 0", "pswpin", 0UL)]
    [DataRow("oom_kill 12", "oom_kill", 12UL)]
    [DataRow("nr_free_pages 258000", "nr_free_pages", 258000UL)]
    [DataRow("pgmajfault 18446744073709551615", "pgmajfault", 18446744073709551615UL)]
    public void LegacyProcLineTryParseVmstatReadsCounters(string line, string counter, ulong value)
    {
        // Act
        var read = LegacyProcLine.TryParseVmstat(line, out var actualCounter, out var actualValue);

        // Assert
        Assert.IsTrue(read, "the line is read");
        Assert.AreEqual(counter, actualCounter, "the counter");
        Assert.AreEqual(value, actualValue, "the value");
    }

    /// <summary>
    /// A line that is not a counter name and an integer is not read.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("pswpin")]
    [DataRow("pswpin -1")]
    [DataRow("pswpin 1.5")]
    [DataRow("pswpin 1e3")]
    [DataRow("pswpin 18446744073709551616")]
    [DataRow("pswpin 123456789012345678901")]
    [DataRow("pswpin 1 2")]
    [DataRow("pswpin x")]
    [DataRow("pswpin ٣")]
    [DataRow("1 pswpin")]
    public void LegacyProcLineTryParseVmstatRefusesOtherForms(string line)
    {
        // Act
        var read = LegacyProcLine.TryParseVmstat(line, out _, out _);

        // Assert
        Assert.IsFalse(read, "the line is not read");
    }

    #endregion // Methods
}