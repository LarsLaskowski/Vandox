using Vandox.Core.Model;

namespace Vandox.Core.Incidents;

/// <summary>
/// Tracks the extreme value of one metric definition.
/// </summary>
internal sealed class MetricTracker
{
    #region Fields

    private readonly string _name;
    private readonly Dictionary<string, string>? _labels;
    private readonly bool _minimum;
    private long _samples;
    private string _source = string.Empty;
    private DateTimeOffset _at;
    private double _value;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricTracker"/> class.
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="labels">The labels a sample must carry exactly, or <c>null</c> for none</param>
    /// <param name="minimum"><c>true</c> to keep the lowest value, <c>false</c> to keep the highest</param>
    internal MetricTracker(string name, Dictionary<string, string>? labels, bool minimum)
    {
        _name = name;
        _labels = labels;
        _minimum = minimum;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Counts a sample when it matches the definition and its value is finite.
    /// </summary>
    /// <param name="source">The source of the record</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="metric">The metric</param>
    internal void Add(string source, DateTimeOffset capturedAt, MetricPoint metric)
    {
        if (Accepts(metric))
        {
            Record(source, capturedAt, metric.Value);
        }
    }

    /// <summary>
    /// Returns the extreme.
    /// </summary>
    /// <returns>The extreme, or <c>null</c> without samples</returns>
    internal MetricExtreme? ToExtreme()
    {
        if (_samples == 0)
        {
            return null;
        }

        return new MetricExtreme
               {
                   Name = _name,
                   Labels = _labels is null ? null : new Dictionary<string, string>(_labels, StringComparer.Ordinal),
                   Extreme = _minimum ? "min" : "max",
                   Source = _source,
                   At = _at,
                   Value = _value,
                   Samples = _samples
               };
    }

    /// <summary>
    /// Tells whether a sample belongs to the definition and has a finite value.
    /// </summary>
    /// <param name="metric">The metric</param>
    /// <returns><c>true</c> when the sample counts</returns>
    private bool Accepts(MetricPoint metric)
    {
        return metric.Name == _name && double.IsFinite(metric.Value) && LabelsMatch(metric.Labels);
    }

    /// <summary>
    /// Counts a sample and keeps it when it is the new extreme.
    /// </summary>
    /// <param name="source">The source of the record</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="value">The value</param>
    private void Record(string source, DateTimeOffset capturedAt, double value)
    {
        _samples++;

        var comparison = value.CompareTo(_value);
        var better = _minimum ? comparison < 0 : comparison > 0;
        var earlier = comparison == 0 && capturedAt < _at;

        if (_samples == 1 || better || earlier)
        {
            _source = source;
            _at = capturedAt;
            _value = value;
        }
    }

    /// <summary>
    /// Tells whether the labels of a sample equal those of the definition.
    /// </summary>
    /// <param name="labels">The labels of the sample</param>
    /// <returns><c>true</c> when both hold the same labels</returns>
    private bool LabelsMatch(Dictionary<string, string>? labels)
    {
        var actual = labels?.Count ?? 0;

        if (_labels is null)
        {
            return actual == 0;
        }

        return labels is not null && actual == _labels.Count && _labels.All(pair => labels.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    #endregion // Methods
}