using Microsoft.Extensions.Primitives;

using Vandox.Storage;

namespace Vandox.Backend.History;

#pragma warning disable RH2003, S2325 // Skeleton: removed with the implementation
/// <summary>
/// The range and the moment of an address: the preset, the start, the end and the moment, with their reading, checks and writing.
/// </summary>
internal sealed class AddressTimes
{
    #region Properties

    /// <summary>
    /// Gets or sets the preset range.
    /// </summary>
    public HistoryPreset? Preset { get; set; }

    /// <summary>
    /// Gets or sets the start of a fixed range.
    /// </summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>
    /// Gets or sets the end of a fixed range.
    /// </summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// Gets or sets the moment.
    /// </summary>
    public DateTimeOffset? At { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Tells whether a name is valid: 1 to 128 bytes of the name pattern of the wire format.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns><c>true</c> when the name is valid</returns>
    internal static bool IsValidName(string? name)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the error of an invalid value.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <returns>The text</returns>
    internal static string Invalid(string name)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the error of a parameter given more than once.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <returns>The text</returns>
    internal static string Repeated(string name)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the parameters range, from, to and at, then checks how the range goes together.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The first error, or an empty text</returns>
    internal string ReadParameters(Dictionary<string, StringValues> values)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks how the preset, the start and the end go together.
    /// </summary>
    /// <returns>The error, or an empty text</returns>
    internal string ValidateRange()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks every value again.
    /// </summary>
    /// <returns>The first error, or an empty text</returns>
    internal string Validate()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Appends the parameters range, from, to and at to the parts of a query string.
    /// </summary>
    /// <param name="parts">The parts</param>
    internal void AppendTo(List<string> parts)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gives the range: the fixed one, else the preset up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <returns>The range</returns>
    internal HistoryRange Range(DateTimeOffset now)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks whether another set has the same preset, start and end.
    /// </summary>
    /// <param name="other">The other set</param>
    /// <returns><c>true</c> when the ranges are the same</returns>
    internal bool SameRange(AddressTimes other)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Copies the set.
    /// </summary>
    /// <returns>The copy</returns>
    internal AddressTimes Copy()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003, S2325 // Skeleton: removed with the implementation