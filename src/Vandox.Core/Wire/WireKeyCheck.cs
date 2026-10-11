using System.Text.Json;

namespace Vandox.Core.Wire;

#pragma warning disable RH2003, S2325

/// <summary>
/// Checks the keys of a parsed wire line before it is bound. One instance belongs to one decoder and keeps its state from line to
/// line; it is used by one caller at a time.
/// </summary>
internal sealed class WireKeyCheck
{
    #region Methods

    /// <summary>
    /// Finds the first key, at any depth, that is not ASCII after unescaping or that repeats another key of its object ignoring ASCII case.
    /// Every key set of the instance is empty again when the method returns.
    /// </summary>
    /// <param name="root">The parsed line, nested at most <see cref="WireFormat.MaxDepth"/> deep</param>
    /// <returns><c>null</c> when every key is fine, otherwise the reason: <c>key not ASCII</c> or <c>duplicate key</c></returns>
    internal string? FindViolation(JsonElement root)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}