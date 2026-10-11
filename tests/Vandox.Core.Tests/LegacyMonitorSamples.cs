namespace Vandox.Core.Tests;

/// <summary>
/// Builds the text of snapshots of the legacy monitoring log for the tests: the summary lines, rows, sections and headers.
/// </summary>
internal static class LegacyMonitorSamples
{
    #region Constants

    /// <summary>
    /// The first line of top: the time and the load averages.
    /// </summary>
    internal const string Load = "top - 09:15:01 up 10 days,  3:02,  1 user,  load average: 0.52, 0.48, 0.45";

    /// <summary>
    /// The line with the CPU percentages.
    /// </summary>
    internal const string Cpu = "%Cpu(s):  3.1 us,  1.2 sy,  0.0 ni, 95.0 id,  0.5 wa,  0.0 hi,  0.2 si,  0.0 st";

    /// <summary>
    /// The line with the memory in KiB.
    /// </summary>
    internal const string Mem = "KiB Mem : 131926528 total,  1234567 free, 80000000 used, 50000000 buff/cache";

    /// <summary>
    /// The line with the swap in KiB and the available memory.
    /// </summary>
    internal const string Swap = "KiB Swap:  8388604 total,  8388604 free,        0 used. 49000000 avail Mem";

    /// <summary>
    /// The header of the process table.
    /// </summary>
    internal const string TableHeader = "  PID USER      PR  NI    VIRT    RES    SHR S  %CPU %MEM     TIME+ COMMAND";

    /// <summary>
    /// The header line of the lsof output.
    /// </summary>
    internal const string LsofHeader = "COMMAND    PID     USER   FD   TYPE   DEVICE SIZE/OFF NODE NAME";

    /// <summary>
    /// The date of the first snapshot of the examples.
    /// </summary>
    internal const string Oct2 = "Fri Oct  2 09:15:01 CEST 2026";

    /// <summary>
    /// The date five minutes after <see cref="Oct2"/>.
    /// </summary>
    internal const string Oct2Later = "Fri Oct  2 09:20:01 CEST 2026";

    /// <summary>
    /// The date ten minutes after <see cref="Oct2"/>.
    /// </summary>
    internal const string Oct2Last = "Fri Oct  2 09:25:01 CEST 2026";

    /// <summary>
    /// The source of every record of the parser.
    /// </summary>
    internal const string Source = "legacy-monitor";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The names of the metrics of the summary lines in the order of their emission.
    /// </summary>
    internal static readonly string[] SummaryNames = [
                                                         "load1",
                                                         "load5",
                                                         "load15",
                                                         "cpu_percent",
                                                         "cpu_percent",
                                                         "cpu_percent",
                                                         "cpu_percent",
                                                         "cpu_percent",
                                                         "cpu_percent",
                                                         "cpu_percent",
                                                         "cpu_percent",
                                                         "mem_total_bytes",
                                                         "mem_free_bytes",
                                                         "mem_used_bytes",
                                                         "mem_buff_cache_bytes",
                                                         "mem_available_bytes",
                                                         "swap_total_bytes",
                                                         "swap_free_bytes",
                                                         "swap_used_bytes"
                                                     ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Builds a snapshot header line.
    /// </summary>
    /// <param name="date">The output of date</param>
    /// <returns>The header line</returns>
    internal static string Header(string date)
    {
        return $"===== {date} =====";
    }

    /// <summary>
    /// Builds the Tasks line.
    /// </summary>
    /// <param name="total">The number of tasks</param>
    /// <returns>The line</returns>
    internal static string Tasks(int total)
    {
        return $"Tasks: {total} total,   1 running, {total - 1} sleeping,   0 stopped,   0 zombie";
    }

    /// <summary>
    /// Builds a row of the process table.
    /// </summary>
    /// <param name="pid">The process ID</param>
    /// <param name="user">The user</param>
    /// <param name="res">The resident memory column</param>
    /// <param name="command">The command column</param>
    /// <param name="cpu">The %CPU column</param>
    /// <returns>The row</returns>
    internal static string Row(int pid, string user, string res, string command, string cpu = "1.5")
    {
        return $"{pid,6} {user,-9} 20   0  500000 {res,7}  10000 S {cpu,5}  0.3   0:12.34 {command}";
    }

    /// <summary>
    /// Builds the lines of the top part of a snapshot: the five summary lines, an empty line, the table header and the rows.
    /// </summary>
    /// <param name="total">The number in the Tasks line</param>
    /// <param name="rows">The rows</param>
    /// <returns>The lines</returns>
    internal static List<string> Top(int total, params string[] rows)
    {
        List<string> lines = [Load, Tasks(total), Cpu, Mem, Swap, string.Empty, TableHeader];

        lines.AddRange(rows);

        return lines;
    }

    /// <summary>
    /// Builds a snapshot in the old format: the header line and the top part with as many tasks as rows.
    /// </summary>
    /// <param name="date">The output of date</param>
    /// <param name="rows">The rows</param>
    /// <returns>The lines</returns>
    internal static List<string> Old(string date, params string[] rows)
    {
        List<string> lines = [Header(date)];

        lines.AddRange(Top(rows.Length, rows));

        return lines;
    }

    /// <summary>
    /// Builds a snapshot in the new format: like <see cref="Old"/>, followed by the given section lines.
    /// </summary>
    /// <param name="date">The output of date</param>
    /// <param name="sections">The lines after the rows</param>
    /// <param name="rows">The rows</param>
    /// <returns>The lines</returns>
    internal static List<string> New(string date, string[] sections, params string[] rows)
    {
        var lines = Old(date, rows);

        lines.AddRange(sections);

        return lines;
    }

    /// <summary>
    /// Builds a socket line of lsof.
    /// </summary>
    /// <param name="command">The command column</param>
    /// <param name="pid">The process ID</param>
    /// <param name="type">IPv4 or IPv6</param>
    /// <param name="device">The device column</param>
    /// <param name="node">TCP or UDP</param>
    /// <param name="name">The name column with the state</param>
    /// <returns>The line</returns>
    internal static string Socket(string command, int pid, string type, int device, string node, string name)
    {
        return $"{command,-9} {pid,5} root       3u {type} {device,8}      0t0  {node} {name}";
    }

    /// <summary>
    /// Joins lines into the text of a file: every line ends in a line feed.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The text</returns>
    internal static string Text(IEnumerable<string> lines)
    {
        return string.Concat(lines.Select(line => line + "\n"));
    }

    #endregion // Methods
}