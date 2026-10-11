namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// Reads the summary lines, the table header and the rows of top.
/// </summary>
internal static class LegacyTopLine
{
    #region Methods

    /// <summary>
    /// Reads the load average line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="load">The load over 1, 5 and 15 minutes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseLoad(string line, out double[] load)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the tasks line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="total">The total number of tasks</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseTasks(string line, out uint total)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the CPU line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="percents">The values of us, sy, ni, id, wa, hi, si and st</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseCpu(string line, out double[] percents)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the memory line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="memory">Total, free, used and buff/cache in bytes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseMemory(string line, out ulong[] memory)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the swap line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="swap">Total, free, used and avail Mem in bytes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseSwap(string line, out ulong[] swap)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Tells whether a line is the header of the process table.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns><c>true</c> for the table header</returns>
    internal static bool IsTableHeader(string line)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads a row of the process table.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The row; <c>null</c> when not read</returns>
    internal static LegacyTopRow? TryParseRow(string line)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads a RES value with its optional suffix.
    /// </summary>
    /// <param name="token">The token</param>
    /// <param name="bytes">The value in bytes</param>
    /// <returns><c>true</c> when read</returns>
    internal static bool TryParseMemoryValue(string token, out ulong bytes)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Scales a decimal number by a power of 1024 in exact integer arithmetic.
    /// </summary>
    /// <param name="integerDigits">The validated ASCII digits of the integer part</param>
    /// <param name="fractionDigits">The validated ASCII digits of the fraction; may be empty</param>
    /// <param name="power">The power of 1024, 0 to 6</param>
    /// <param name="bytes">The value rounded half away from zero</param>
    /// <returns><c>true</c> when the value fits 64 bits</returns>
    internal static bool TryScaleBinary(string integerDigits, string fractionDigits, int power, out ulong bytes)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}