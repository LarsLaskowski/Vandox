namespace Vandox.Core.Configuration;

/// <summary>
/// Configures where the backend keeps its data.
/// </summary>
public sealed class StorageConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets the directory of the data: an absolute, clean path.
    /// </summary>
    [ConfigKey("directory")]
    public string Directory { get; set; } = "/data";

    #endregion // Properties
}