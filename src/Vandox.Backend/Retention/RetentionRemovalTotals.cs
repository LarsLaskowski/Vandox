using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Storage;

namespace Vandox.Backend.Retention;

/// <summary>
/// The sums of the counts of the steps of one removal pass.
/// </summary>
internal sealed class RetentionRemovalTotals
{
    #region Fields

    private int _metricValues;
    private int _logLines;
    private int _snapshots;
    private int _events;
    private int _gaps;
    private int _fiveMinuteValues;
    private int _hourlyValues;
    private int _templateHours;
    private int _incidents;
    private int _holds;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Adds the counts of a step.
    /// </summary>
    /// <param name="step">The result of the step</param>
    internal void Add(RemovalStepResult step)
    {
        _metricValues = Sum(_metricValues, step.MetricValues);
        _logLines = Sum(_logLines, step.LogLines);
        _snapshots = Sum(_snapshots, step.Snapshots);
        _events = Sum(_events, step.EventRecords);
        _gaps = Sum(_gaps, step.Gaps);
        _fiveMinuteValues = Sum(_fiveMinuteValues, step.FiveMinuteValues);
        _hourlyValues = Sum(_hourlyValues, step.HourlyValues);
        _templateHours = Sum(_templateHours, step.TemplateHours);
        _incidents = Sum(_incidents, step.RemovedIncidents);
        _holds = Sum(_holds, step.Holds);
    }

    /// <summary>
    /// Logs the line of the records and the line of the derived data, each when one of its counts is not zero.
    /// </summary>
    /// <param name="logger">The logger</param>
    internal void Log(ILogger logger)
    {
        if (new[] { _metricValues, _logLines, _snapshots, _events, _gaps }.Any(count => count > 0))
        {
            logger.RetentionRecordsRemoved(_metricValues, _logLines, _snapshots, _events, _gaps);
        }

        if (new[] { _fiveMinuteValues, _hourlyValues, _templateHours, _incidents, _holds }.Any(count => count > 0))
        {
            logger.RetentionDerivedRemoved(_fiveMinuteValues, _hourlyValues, _templateHours, _incidents, _holds);
        }
    }

    /// <summary>
    /// Adds two counts, at most the largest <see cref="int"/>.
    /// </summary>
    /// <param name="total">The sum so far</param>
    /// <param name="count">The count to add</param>
    /// <returns>The sum</returns>
    private static int Sum(int total, int count)
    {
        return (int)Math.Min((long)total + count, int.MaxValue);
    }

    #endregion // Methods
}