namespace Vandox.Core.Configuration;

/// <summary>
/// Names the key of an option or a section in the configuration file.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigKeyAttribute : Attribute
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigKeyAttribute"/> class.
    /// </summary>
    /// <param name="name">The key</param>
    public ConfigKeyAttribute(string name)
    {
        Name = name;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the key.
    /// </summary>
    public string Name { get; }

    #endregion // Properties
}