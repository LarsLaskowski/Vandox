namespace Vandox.Core.Model;

/// <summary>
/// Bounds shared by all record types.
/// </summary>
public static class ModelLimits
{
    #region Constants

    /// <summary>
    /// Maximum length of a name (source, metric name, label key, collector) in UTF-8 bytes.
    /// </summary>
    public const int MaxNameBytes = 128;

    /// <summary>
    /// Maximum length of a short text in UTF-8 bytes.
    /// </summary>
    public const int MaxShortTextBytes = 1024;

    /// <summary>
    /// Maximum length of a text in UTF-8 bytes.
    /// </summary>
    public const int MaxTextBytes = 16384;

    /// <summary>
    /// Maximum number of entries of a list or map.
    /// </summary>
    public const int MaxItems = 4096;

    /// <summary>
    /// Maximum number of labels of a metric.
    /// </summary>
    public const int MaxLabels = 32;

    #endregion // Constants
}