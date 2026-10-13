namespace Vandox.Backend.Logs;

#pragma warning disable RH2003, S2325

/// <summary>
/// The names of the syslog priorities.
/// </summary>
public static class Severities
{
    #region Methods

    /// <summary>
    /// Reads a priority name.
    /// </summary>
    /// <param name="name">The name</param>
    /// <param name="priority">The priority, 0 to 7</param>
    /// <returns><c>true</c> when the name is one of the eight names</returns>
    public static bool TryParse(string name, out byte priority)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the name of a priority.
    /// </summary>
    /// <param name="priority">The priority, 0 to 7</param>
    /// <returns>The name</returns>
    public static string Name(byte priority)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}