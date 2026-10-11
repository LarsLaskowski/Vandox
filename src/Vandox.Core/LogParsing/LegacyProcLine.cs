namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// Reads the lines of the meminfo, pressure and vmstat sections.
/// </summary>
internal static class LegacyProcLine
{
    #region Methods

    /// <summary>
    /// Reads a meminfo line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="field">The field name</param>
    /// <param name="value">The byte count for kB, else the number</param>
    /// <param name="kilobytes">Whether the line had the unit kB</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseMeminfo(string line, out string field, out ulong value, out bool kilobytes)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Tells whether a pressure line says that the pressure is not available.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for <c>n/a</c> or <c>&lt;res&gt;: n/a</c></returns>
    internal static bool IsNotAvailable(string line)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads a pressure line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="previousResource">The resource of the previous line that had only a some part; <c>null</c> when none</param>
    /// <returns>The pressure entries of the line; <c>null</c> when not read</returns>
    internal static IReadOnlyList<LegacyPressure>? TryParsePressure(string line, string? previousResource)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads a vmstat line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="counter">The counter name</param>
    /// <param name="value">The value</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseVmstat(string line, out string counter, out ulong value)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}