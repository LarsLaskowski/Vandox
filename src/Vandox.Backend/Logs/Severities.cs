namespace Vandox.Backend.Logs;

/// <summary>
/// The names of the syslog priorities.
/// </summary>
public static class Severities
{
    #region Fields

    private static readonly string[] _names = ["emerg", "alert", "crit", "err", "warning", "notice", "info", "debug"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Reads a priority name.
    /// </summary>
    /// <param name="name">The name</param>
    /// <param name="priority">The priority, 0 to 7</param>
    /// <returns><c>true</c> when the name is one of the eight names</returns>
    public static bool TryParse(string name, out byte priority)
    {
        var index = Array.IndexOf(_names, name);

        priority = index < 0 ? (byte)0 : (byte)index;

        return index >= 0;
    }

    /// <summary>
    /// Returns the name of a priority.
    /// </summary>
    /// <param name="priority">The priority, 0 to 7</param>
    /// <returns>The name</returns>
    /// <exception cref="ArgumentOutOfRangeException">The priority is above 7</exception>
    public static string Name(byte priority)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, (byte)7);

        return _names[priority];
    }

    #endregion // Methods
}