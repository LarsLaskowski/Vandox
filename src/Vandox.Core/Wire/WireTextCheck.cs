namespace Vandox.Core.Wire;

#pragma warning disable RH2003, S2325, S1144

/// <summary>
/// Checks the raw bytes of a wire line for valid UTF-8 and for surrogate escapes that are not a high half directly followed by an escaped low half.
/// </summary>
internal static class WireTextCheck
{
    #region Constants

    /// <summary>
    /// The reason for a line that is not valid UTF-8.
    /// </summary>
    internal const string NotUtf8 = "line not valid UTF-8";

    /// <summary>
    /// The reason for a line with an unpaired surrogate escape.
    /// </summary>
    internal const string UnpairedSurrogate = "unpaired surrogate escape";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Finds the first violation of the text rule in a line.
    /// </summary>
    /// <param name="line">The bytes of the line</param>
    /// <returns><c>null</c> when the line keeps the rule, otherwise <see cref="NotUtf8"/> or <see cref="UnpairedSurrogate"/></returns>
    internal static string? FindViolation(ReadOnlySpan<byte> line)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the <c>\uXXXX</c> escape that starts at <paramref name="index"/>.
    /// </summary>
    /// <param name="line">The bytes of the line</param>
    /// <param name="index">The position of the backslash</param>
    /// <param name="codeUnit">The UTF-16 code unit of the escape</param>
    /// <returns><c>true</c> when six bytes of a complete escape with four hex digits start there</returns>
    private static bool TryReadEscape(ReadOnlySpan<byte> line, int index, out int codeUnit)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}