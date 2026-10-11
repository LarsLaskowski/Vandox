using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Creates the records of the legacy monitoring log.
/// </summary>
internal static class LegacyMetrics
{
    #region Constants

    private const string Percent = "percent";
    private const string Bytes = "bytes";

    #endregion // Constants

    #region Fields

    private static readonly string[] _cpuModes = ["user", "system", "nice", "idle", "iowait", "irq", "softirq", "steal"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Wraps a payload in an import record.
    /// </summary>
    /// <param name="capturedAt">The instant of the snapshot</param>
    /// <param name="data">The payload</param>
    /// <returns>The record</returns>
    internal static DataRecord Record(DateTimeOffset capturedAt, IPayload data)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = LegacyMonitorParser.ParserType,
                   CapturedAt = capturedAt,
                   Data = data
               };
    }

    /// <summary>
    /// Creates a metric record.
    /// </summary>
    /// <param name="capturedAt">The instant of the snapshot</param>
    /// <param name="name">The metric name</param>
    /// <param name="value">The value</param>
    /// <param name="unit">The unit; empty for none</param>
    /// <param name="labels">The labels as pairs of key and value</param>
    /// <returns>The record</returns>
    internal static DataRecord Metric(DateTimeOffset capturedAt, string name, double value, string unit, params string[] labels)
    {
        Dictionary<string, string>? map = null;

        if (labels.Length > 0)
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var index = 0; index < labels.Length; index += 2)
            {
                map[labels[index]] = labels[index + 1];
            }
        }

        return Record(capturedAt,
                      new MetricPoint
                      {
                          Name = name,
                          Value = value,
                          Unit = unit,
                          Labels = map
                      });
    }

    /// <summary>
    /// Adds the metrics of the summary lines of top.
    /// </summary>
    /// <param name="ready">Receives the records</param>
    /// <param name="capturedAt">The instant of the snapshot</param>
    /// <param name="load">The three load averages</param>
    /// <param name="cpu">The eight CPU percentages</param>
    /// <param name="memory">Total, free, used and buff/cache in bytes</param>
    /// <param name="swap">Total, free and used swap and the available memory in bytes</param>
    internal static void AddSummary(List<DataRecord> ready, DateTimeOffset capturedAt, double[] load, double[] cpu, ulong[] memory, ulong[] swap)
    {
        ready.Add(Metric(capturedAt, "load1", load[0], string.Empty));
        ready.Add(Metric(capturedAt, "load5", load[1], string.Empty));
        ready.Add(Metric(capturedAt, "load15", load[2], string.Empty));

        for (var index = 0; index < _cpuModes.Length; index++)
        {
            ready.Add(Metric(capturedAt, "cpu_percent", cpu[index], Percent, "mode", _cpuModes[index]));
        }

        ready.Add(Metric(capturedAt, "mem_total_bytes", memory[0], Bytes));
        ready.Add(Metric(capturedAt, "mem_free_bytes", memory[1], Bytes));
        ready.Add(Metric(capturedAt, "mem_used_bytes", memory[2], Bytes));
        ready.Add(Metric(capturedAt, "mem_buff_cache_bytes", memory[3], Bytes));
        ready.Add(Metric(capturedAt, "mem_available_bytes", swap[3], Bytes));
        ready.Add(Metric(capturedAt, "swap_total_bytes", swap[0], Bytes));
        ready.Add(Metric(capturedAt, "swap_free_bytes", swap[1], Bytes));
        ready.Add(Metric(capturedAt, "swap_used_bytes", swap[2], Bytes));
    }

    /// <summary>
    /// Adds the metrics of one pressure entry.
    /// </summary>
    /// <param name="ready">Receives the records</param>
    /// <param name="capturedAt">The instant of the snapshot</param>
    /// <param name="entry">The entry</param>
    internal static void AddPressure(List<DataRecord> ready, DateTimeOffset capturedAt, LegacyPressure entry)
    {
        string[] labels = ["resource", entry.Resource, "kind", entry.Kind];

        ready.Add(Metric(capturedAt, "pressure_avg10", entry.Avg10, Percent, labels));
        ready.Add(Metric(capturedAt, "pressure_avg60", entry.Avg60, Percent, labels));
        ready.Add(Metric(capturedAt, "pressure_avg300", entry.Avg300, Percent, labels));
        ready.Add(Metric(capturedAt, "pressure_stall_microseconds_total", entry.Total, "microseconds", labels));
    }

    #endregion // Methods
}