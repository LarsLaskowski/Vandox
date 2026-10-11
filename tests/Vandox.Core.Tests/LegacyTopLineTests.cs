using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LegacyTopLine"/>
/// </summary>
[TestClass]
public class LegacyTopLineTests
{
    #region Constants

    private const string LoadKind = "load";
    private const string TasksKind = "tasks";
    private const string CpuKind = "cpu";
    private const string MemoryKind = "memory";
    private const string SwapKind = "swap";
    private const double Delta = 1e-9;
    private const string FourMemoryValues = "200 free, 300 used, 500 buff/cache";
    private const string ZeroMemoryValues = "0 free, 0 used, 0 buff/cache";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The three load averages are read from the first line of top.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="load1">The expected load of one minute</param>
    /// <param name="load5">The expected load of five minutes</param>
    /// <param name="load15">The expected load of fifteen minutes</param>
    [TestMethod]
    [DataRow("top - 09:15:01 up 10 days,  3:02,  1 user,  load average: 0.52, 0.48, 0.45", 0.52, 0.48, 0.45)]
    [DataRow("top - 09:15:01 up 1 min,  0 users,  load average: 0.00, 0.01, 0.05", 0.0, 0.01, 0.05)]
    [DataRow("top - 03:00:00 up 400 days, 12:00,  2 users,  load average: 12.5, 100, 999999999999999.999999", 12.5, 100.0, 999999999999999.999999)]
    [DataRow("top - 09:15:01 up 10 days,  3:02,  1 user,  load average:0.52,0.48,0.45", 0.52, 0.48, 0.45)]
    public void LegacyTopLineTryParseLoadReadsThreeValues(string line, double load1, double load5, double load15)
    {
        // Act
        var read = LegacyTopLine.TryParseLoad(line, out var load);

        // Assert
        Assert.IsTrue(read, "the line is read");
        Assert.HasCount(3, load, "three values");
        Assert.AreEqual(load1, load[0], Delta, "load of one minute");
        Assert.AreEqual(load5, load[1], Delta, "load of five minutes");
        Assert.AreEqual(load15, load[2], Delta, "load of fifteen minutes");
    }

    /// <summary>
    /// The number of tasks is read from the Tasks line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expected">The expected total</param>
    [TestMethod]
    [DataRow("Tasks: 312 total,   1 running, 311 sleeping,   0 stopped,   0 zombie", 312u)]
    [DataRow("Tasks:   1 total,   1 running,   0 sleeping,   0 stopped,   0 zombie", 1u)]
    [DataRow("Tasks:4294967295 total,   1 running", 4294967295u)]
    public void LegacyTopLineTryParseTasksReadsTotal(string line, uint expected)
    {
        // Act
        var read = LegacyTopLine.TryParseTasks(line, out var total);

        // Assert
        Assert.IsTrue(read, "the line is read");
        Assert.AreEqual(expected, total, "the total");
    }

    /// <summary>
    /// The eight CPU percentages are read in the order us, sy, ni, id, wa, hi, si, st, also without a space after a comma or the colon.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expected">The expected percentages as a text of eight numbers</param>
    [TestMethod]
    [DataRow("%Cpu(s):  3.1 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st", "3.1 1.2 0.0 95.0 0.5 0.0 0.2 0.0")]
    [DataRow("%Cpu(s):0.0 us,0.0 sy,0.0 ni,100.0 id,0.0 wa,0.0 hi,0.0 si,0.0 st", "0 0 0 100 0 0 0 0")]
    [DataRow("%Cpu(s): 12 us, 8 sy, 1 ni, 70 id, 5 wa, 2 hi, 1 si, 1 st", "12 8 1 70 5 2 1 1")]
    [DataRow("%Cpu(s): 100.0 us,  0.0 sy,  0.0 ni,  0.0 id,  0.0 wa,  0.0 hi,  0.0 si,  0.0 st", "100 0 0 0 0 0 0 0")]
    public void LegacyTopLineTryParseCpuReadsEightModes(string line, string expected)
    {
        // Arrange
        var values = expected.Split(' ').Select(text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        // Act
        var read = LegacyTopLine.TryParseCpu(line, out var percents);

        // Assert
        Assert.IsTrue(read, "the line is read");
        Assert.HasCount(8, percents, "eight values");

        for (var index = 0; index < values.Length; index++)
        {
            Assert.AreEqual(values[index], percents[index], Delta, $"value {index}");
        }
    }

    /// <summary>
    /// The memory line is read by the labels, in bytes, as total, free, used, buff/cache, whatever the unit and the order.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="total">The expected total</param>
    /// <param name="free">The expected free memory</param>
    /// <param name="used">The expected used memory</param>
    /// <param name="cache">The expected buff/cache</param>
    [TestMethod]
    [DataRow("KiB Mem : 131926528 total,  1234567 free, 80000000 used, 50000000 buff/cache", 135092764672UL, 1264196608UL, 81920000000UL, 51200000000UL)]
    [DataRow("KiB Mem :131926528 total,  1234567 free, 80000000 used, 50000000 buff/cache", 135092764672UL, 1264196608UL, 81920000000UL, 51200000000UL)]
    [DataRow("KiB Mem :131926528 total,1234567 free,80000000 used,50000000 buff/cache", 135092764672UL, 1264196608UL, 81920000000UL, 51200000000UL)]
    [DataRow("KiB Mem : 1000 total, 300 used, 200 free, 500 buff/cache", 1024000UL, 204800UL, 307200UL, 512000UL)]
    [DataRow("MiB Mem :  128834.5 total,    120.6 free,  78125.0 used,  48828.1 buff/cache", 135092764672UL, 126458266UL, 81920000000UL, 51199973786UL)]
    [DataRow("GiB Mem :    125.8 total,      1.2 free,     76.3 used,     47.7 buff/cache", 135076721459UL, 1288490189UL, 81926501171UL, 51217485005UL)]
    [DataRow("KiB Mem : 0 total, 0 free, 0 used, 0 buff/cache", 0UL, 0UL, 0UL, 0UL)]
    public void LegacyTopLineTryParseMemoryReadsUnitsAndLabels(string line, ulong total, ulong free, ulong used, ulong cache)
    {
        // Act
        var read = LegacyTopLine.TryParseMemory(line, out var memory);

        // Assert
        Assert.IsTrue(read, "the line is read");
        Assert.AreSequenceEqual(new[] { total, free, used, cache }, memory, "total, free, used, buff/cache in bytes");
    }

    /// <summary>
    /// The swap line gives total, free, used and the available memory after the full stop, in bytes.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="total">The expected total</param>
    /// <param name="free">The expected free swap</param>
    /// <param name="used">The expected used swap</param>
    /// <param name="available">The expected available memory</param>
    [TestMethod]
    [DataRow("KiB Swap:  8388604 total,  8388604 free,        0 used. 49000000 avail Mem", 8589930496UL, 8589930496UL, 0UL, 50176000000UL)]
    [DataRow("KiB Swap:  8388604 total,  8388604 free,        0 used. 49000000 avail Mem ", 8589930496UL, 8589930496UL, 0UL, 50176000000UL)]
    [DataRow("MiB Swap:      0.0 total,      0.0 free,      0.0 used.  50000.0 avail Mem", 0UL, 0UL, 0UL, 52428800000UL)]
    [DataRow("KiB Swap:8388604 total,8388604 free,0 used.49000000 avail Mem", 8589930496UL, 8589930496UL, 0UL, 50176000000UL)]
    [DataRow("KiB Swap: 50 free, 200 total, 100 used. 70 avail Mem", 204800UL, 51200UL, 102400UL, 71680UL)]
    public void LegacyTopLineTryParseSwapReadsAvailMem(string line, ulong total, ulong free, ulong used, ulong available)
    {
        // Act
        var read = LegacyTopLine.TryParseSwap(line, out var swap);

        // Assert
        Assert.IsTrue(read, "the line is read");
        Assert.AreSequenceEqual(new[] { total, free, used, available }, swap, "total, free, used, avail Mem in bytes");
    }

    /// <summary>
    /// A summary line with a missing or repeated label, a number outside the plain decimal form, or a byte count that does not fit 64 bits is not read.
    /// </summary>
    /// <param name="kind">Which summary line the text is</param>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  1 user")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: 0.52, 0.48")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: NaN, 0.48, 0.45")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: 1e3, 0.48, 0.45")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: -0.1, 0.48, 0.45")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: 1234567890123456.0, 0.48, 0.45")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: 0.1234567, 0.48, 0.45")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: 0,52, 0,48, 0,45")]
    [DataRow(LoadKind, "top - 09:15:01 up 1 day,  load average: ٣.0, 0.48, 0.45")]
    [DataRow(TasksKind, "Tasks: 312 running, 311 sleeping")]
    [DataRow(TasksKind, "Tasks: abc total")]
    [DataRow(TasksKind, "Tasks: -1 total")]
    [DataRow(TasksKind, "Tasks: 4294967296 total")]
    [DataRow(CpuKind, "%Cpu(s):  3.1 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si")]
    [DataRow(CpuKind, "%Cpu(s):  3.1 us,  3.1 us,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st")]
    [DataRow(CpuKind, "%Cpu(s):  3.1 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st,  0.0 xx")]
    [DataRow(CpuKind, "%Cpu(s):  NaN us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st")]
    [DataRow(CpuKind, "%Cpu(s):  1e3 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st")]
    [DataRow(CpuKind, "%Cpu(s):  1,5 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st")]
    [DataRow(CpuKind, "%Cpu(s):  ٣.0 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st")]
    [DataRow(CpuKind, "%Cpu(s):  -0.1 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st")]
    [DataRow(CpuKind, "%Cpu(s):  0.1234567 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st")]
    [DataRow(MemoryKind, "KiB Mem : 1000 total, 200 free, 300 used")]
    [DataRow(MemoryKind, "KiB Mem : 1000 total, 200 free, 200 free, 500 buff/cache")]
    [DataRow(MemoryKind, "KiB Mem : 1000 total, " + FourMemoryValues + ", 5 free")]
    [DataRow(MemoryKind, "KiB Mem : NaN total, " + FourMemoryValues)]
    [DataRow(MemoryKind, "KiB Mem : 1e3 total, " + FourMemoryValues)]
    [DataRow(MemoryKind, "KiB Mem : 1,5 total, " + FourMemoryValues)]
    [DataRow(MemoryKind, "KiB Mem : ٣ total, " + FourMemoryValues)]
    [DataRow(MemoryKind, "KiB Mem : -1 total, " + FourMemoryValues)]
    [DataRow(MemoryKind, "KiB Mem : 1234567890123456 total, " + FourMemoryValues)]
    [DataRow(MemoryKind, "KiB Mem : 0.1234567 total, " + FourMemoryValues)]
    [DataRow(MemoryKind, "EiB Mem : 16.0 total, " + ZeroMemoryValues)]
    [DataRow(MemoryKind, "GiB Mem : 999999999999999.0 total, " + ZeroMemoryValues)]
    [DataRow(MemoryKind, "EiB Mem : 999999999999999.999999 total, " + ZeroMemoryValues)]
    [DataRow(MemoryKind, "PiB Mem : 999999999999999.999999 total, " + ZeroMemoryValues)]
    [DataRow(MemoryKind, "KiB Swap: 1 total, 1 free, 0 used. 5 avail Mem")]
    [DataRow(SwapKind, "KiB Swap: 1 total, 1 free, 0 used. 5 avail")]
    [DataRow(SwapKind, "KiB Swap: 1 total, 1 free, 1 free, 0 used. 5 avail Mem")]
    [DataRow(SwapKind, "KiB Swap: NaN total, 1 free, 0 used. 5 avail Mem")]
    [DataRow(SwapKind, "KiB Swap: 1 total, 1 free, 0 used. 1e3 avail Mem")]
    [DataRow(SwapKind, "EiB Swap: 16.0 total, 0 free, 0 used. 5 avail Mem")]
    [DataRow(SwapKind, "EiB Swap: 1 total, 1 free, 0 used. 999999999999999.999999 avail Mem")]
    public void LegacyTopLineTryParseRefusesMalformedSummaryLines(string kind, string line)
    {
        // Act
        var read = kind switch
                   {
                       LoadKind => LegacyTopLine.TryParseLoad(line, out _),
                       TasksKind => LegacyTopLine.TryParseTasks(line, out _),
                       CpuKind => LegacyTopLine.TryParseCpu(line, out _),
                       MemoryKind => LegacyTopLine.TryParseMemory(line, out _),
                       _ => LegacyTopLine.TryParseSwap(line, out _)
                   };

        // Assert
        Assert.IsFalse(read, "the line is not read");
    }

    /// <summary>
    /// A summary line that is not of its kind is not read by the other parsers.
    /// </summary>
    [TestMethod]
    public void LegacyTopLineTryParseRefusesLinesOfOtherKinds()
    {
        // Act
        var results = new[]
                      {
                          LegacyTopLine.TryParseLoad(LegacyMonitorSamples.Cpu, out _),
                          LegacyTopLine.TryParseTasks(LegacyMonitorSamples.Cpu, out _),
                          LegacyTopLine.TryParseCpu(LegacyMonitorSamples.Mem, out _),
                          LegacyTopLine.TryParseMemory(LegacyMonitorSamples.Swap, out _),
                          LegacyTopLine.TryParseSwap(LegacyMonitorSamples.Mem, out _)
                      };

        // Assert
        Assert.DoesNotContain(true, results, "none of the lines is read as another kind");
    }

    /// <summary>
    /// The RES column converts to bytes by its suffix with exact arithmetic, rounded half away from zero.
    /// </summary>
    /// <param name="token">The RES column</param>
    /// <param name="expected">The expected number of bytes</param>
    [TestMethod]
    [DataRow("0", 0UL)]
    [DataRow("1234", 1263616UL)]
    [DataRow("10k", 10240UL)]
    [DataRow("123.4m", 129394278UL)]
    [DataRow("1.234g", 1324997411UL)]
    [DataRow("2.000t", 2199023255552UL)]
    [DataRow("1.5p", 1688849860263936UL)]
    [DataRow("1.000e", 1152921504606846976UL)]
    [DataRow("0.001m", 1049UL)]
    [DataRow("15.999e", 18445591152204944769UL)]
    [DataRow("2.5m", 2621440UL)]
    public void LegacyTopLineTryParseMemoryValueConvertsSuffixes(string token, ulong expected)
    {
        // Act
        var read = LegacyTopLine.TryParseMemoryValue(token, out var bytes);

        // Assert
        Assert.IsTrue(read, "the token is read");
        Assert.AreEqual(expected, bytes, "the number of bytes");
    }

    /// <summary>
    /// Anything but digits, a fraction of at most three digits and one lower-case suffix is no memory value; a value above 64 bits is no value and never an exception.
    /// </summary>
    /// <param name="token">The RES column</param>
    [TestMethod]
    [DataRow("?")]
    [DataRow("")]
    [DataRow("m")]
    [DataRow("1.2M")]
    [DataRow("1.2345g")]
    [DataRow("-1")]
    [DataRow("+1")]
    [DataRow("123456789012345678901")]
    [DataRow("1e3")]
    [DataRow("1.")]
    [DataRow(".5")]
    [DataRow("1.5.5")]
    [DataRow("1,5m")]
    [DataRow("1 m")]
    [DataRow("1x")]
    [DataRow("1mm")]
    [DataRow("٣m")]
    [DataRow("16.000e")]
    [DataRow("99999999999999999999.999e")]
    [DataRow("99999999999999999999.999p")]
    [DataRow("99999999999999999999.999g")]
    [DataRow("18014398509481984")]
    public void LegacyTopLineTryParseMemoryValueRefusesOtherForms(string token)
    {
        // Act
        var read = LegacyTopLine.TryParseMemoryValue(token, out _);

        // Assert
        Assert.IsFalse(read, "the token is not read");
    }

    /// <summary>
    /// Scaling is exact, rounds half away from zero, and refuses a result above 64 bits before or after the rounding.
    /// </summary>
    /// <param name="integerDigits">The digits before the point</param>
    /// <param name="fraction">The digits after the point</param>
    /// <param name="power">The power of 1,024</param>
    /// <param name="expected">The expected number of bytes, or -1 for a refusal</param>
    [TestMethod]
    [DataRow("1234", "", 0, "1234")]
    [DataRow("0", "4", 0, "0")]
    [DataRow("0", "5", 0, "1")]
    [DataRow("1", "5", 0, "2")]
    [DataRow("2", "5", 0, "3")]
    [DataRow("1", "5", 2, "1572864")]
    [DataRow("1", "0", 6, "1152921504606846976")]
    [DataRow("15", "999", 6, "18445591152204944769")]
    [DataRow("16", "", 6, "-1")]
    [DataRow("17", "", 6, "-1")]
    [DataRow("16", "000", 6, "-1")]
    [DataRow("18446744073709551615", "", 0, "18446744073709551615")]
    [DataRow("18446744073709551615", "5", 0, "-1")]
    [DataRow("18446744073709551616", "", 0, "-1")]
    [DataRow("99999999999999999999", "999", 6, "-1")]
    [DataRow("99999999999999999999", "999", 5, "-1")]
    [DataRow("999999999999999", "999999", 6, "-1")]
    [DataRow("1234567890123456789012345678901", "", 0, "-1")]
    [DataRow("1", "1234567890123456789012345678901", 0, "-1")]
    public void LegacyTopLineTryScaleBinaryRoundsAndRefusesOverflow(string integerDigits, string fraction, int power, string expected)
    {
        // Act
        var read = LegacyTopLine.TryScaleBinary(integerDigits, fraction, power, out var bytes);

        // Assert
        Assert.AreEqual(expected != "-1", read, "whether the value fits 64 bits");

        if (read)
        {
            Assert.AreEqual(ulong.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), bytes, "the number of bytes");
        }
    }

    /// <summary>
    /// A row of the process table is read column by column; the command is the rest of the line.
    /// </summary>
    /// <param name="line">The row</param>
    /// <param name="pid">The expected process ID</param>
    /// <param name="user">The expected user</param>
    /// <param name="state">The expected state</param>
    /// <param name="cpu">The expected %CPU</param>
    /// <param name="res">The expected resident bytes</param>
    /// <param name="command">The expected command</param>
    [TestMethod]
    [DataRow("   1234 www-data  20   0  500000 123456  10000 S   1.5  0.3   0:12.34 apache2", 1234, "www-data", "S", 1.5, 126418944UL, "apache2")]
    [DataRow("     1 root      rt -20  100000   2.5m   9000 R  99.9  0.1  12:34.56 migration/0", 1, "root", "R", 99.9, 2621440UL, "migration/0")]
    [DataRow("   4711 psaadm    20   0 2410000   1.2g  12044 S   0.0  0.1   1:00.00 php-fpm: pool www", 4711, "psaadm", "S", 0.0, 1288490189UL, "php-fpm: pool www")]
    [DataRow("2147483647 mysql   20   0  512340    300  12044 Z 100  0.1 100:00.00 /usr/sbin/mariadbd --user=mysql +", 2147483647, "mysql", "Z", 100.0, 307200UL, "/usr/sbin/mariadbd --user=mysql +")]
    [DataRow("  7 u 20 0 1 0 1 I 0.0 0.0 0:00.00 [kworker/0:1-events]", 7, "u", "I", 0.0, 0UL, "[kworker/0:1-events]")]
    [DataRow("  8 u 20 0 1 5 1 D 0.0 0.0 0:00.00 spaced   command  here", 8, "u", "D", 0.0, 5120UL, "spaced   command  here")]
    public void LegacyTopLineTryParseRowReadsColumns(string line, int pid, string user, string state, double cpu, ulong res, string command)
    {
        // Act
        var row = LegacyTopLine.TryParseRow(line);

        // Assert
        Assert.IsNotNull(row, "a row");
        Assert.AreEqual(pid, row.Pid, "process ID");
        Assert.AreEqual(user, row.User, "user");
        Assert.AreEqual(state, row.State, "state");
        Assert.AreEqual(cpu, row.CpuPercent, Delta, "%CPU");
        Assert.AreEqual(res, row.ResBytes, "resident bytes");
        Assert.AreEqual(command, row.Command, "command");
    }

    /// <summary>
    /// A line that is not a row of the process table is not read.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("garbage")]
    [DataRow("  100 root 20 0 1 1 1 S 1.0 0.1 0:00.01")]
    [DataRow("  abc root 20 0 1 1 1 S 1.0 0.1 0:00.01 x")]
    [DataRow("  0 root 20 0 1 1 1 S 1.0 0.1 0:00.01 x")]
    [DataRow("  -5 root 20 0 1 1 1 S 1.0 0.1 0:00.01 x")]
    [DataRow("  2147483648 root 20 0 1 1 1 S 1.0 0.1 0:00.01 x")]
    [DataRow("  12345678901 root 20 0 1 1 1 S 1.0 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 1 1 SS 1.0 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 1 1 1 1.0 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 1 1 S NaN 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 1 1 S 1e3 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 1 1 S -1.0 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 1 1 S 1,5 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 ? 1 S 1.0 0.1 0:00.01 x")]
    [DataRow("  100 root 20 0 1 99999999999999999999.999e 1 S 1.0 0.1 0:00.01 x")]
    [DataRow("  PID USER      PR  NI    VIRT    RES    SHR S  %CPU %MEM     TIME+ COMMAND")]
    public void LegacyTopLineTryParseRowRefusesMalformedRows(string line)
    {
        // Act
        var row = LegacyTopLine.TryParseRow(line);

        // Assert
        Assert.IsNull(row, "no row");
    }

    /// <summary>
    /// A token before the command that is longer than 64 bytes makes the row unreadable.
    /// </summary>
    [TestMethod]
    public void LegacyTopLineTryParseRowRefusesTokenOver64Bytes()
    {
        // Arrange
        var longUser = new string('u', 65);
        var okUser = new string('u', 64);

        // Act
        var refused = LegacyTopLine.TryParseRow($"  100 {longUser} 20 0 1 1 1 S 1.0 0.1 0:00.01 x");
        var accepted = LegacyTopLine.TryParseRow($"  100 {okUser} 20 0 1 1 1 S 1.0 0.1 0:00.01 x");

        // Assert
        Assert.IsNull(refused, "a token of 65 bytes is refused");
        Assert.IsNotNull(accepted, "a token of 64 bytes is read");
    }

    /// <summary>
    /// The header of the process table is recognized by its column names, whatever the spacing.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expected">Whether the line is the table header</param>
    [TestMethod]
    [DataRow("  PID USER      PR  NI    VIRT    RES    SHR S  %CPU %MEM     TIME+ COMMAND", true)]
    [DataRow("    PID USER      PR  NI    VIRT    RES    SHR S  %CPU  %MEM     TIME+ COMMAND", true)]
    [DataRow("PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ COMMAND", true)]
    [DataRow("  PID USER      PR  NI    VIRT    RES    SHR S  %CPU %MEM     TIME+", false)]
    [DataRow("   100 root      20   0  500000 123456  10000 S   1.5  0.3   0:12.34 apache2", false)]
    [DataRow("COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE NAME", false)]
    [DataRow("", false)]
    public void LegacyTopLineIsTableHeaderMatchesColumnNames(string line, bool expected)
    {
        // Act
        var actual = LegacyTopLine.IsTableHeader(line);

        // Assert
        Assert.AreEqual(expected, actual, "whether the line is the table header");
    }

    #endregion // Methods
}