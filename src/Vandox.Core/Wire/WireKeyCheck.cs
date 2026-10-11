using System.Text;
using System.Text.Json;

namespace Vandox.Core.Wire;

/// <summary>
/// Checks the keys of a parsed wire line before it is bound. One instance belongs to one decoder and keeps its state from line to
/// line; it is used by one caller at a time.
/// </summary>
internal sealed class WireKeyCheck
{
    #region Constants

    private const int KeptKeys = 64;

    private const string NotAscii = "key not ASCII";

    private const string Duplicate = "duplicate key";

    #endregion // Constants

    #region Fields

    private readonly HashSet<string>?[] _sets = new HashSet<string>?[WireFormat.MaxDepth];
    private readonly List<string>?[] _keys = new List<string>?[WireFormat.MaxDepth];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Finds the first key, at any depth, that is not ASCII after unescaping or that repeats another key of its object ignoring ASCII case.
    /// Every key set of the instance is empty again when the method returns.
    /// </summary>
    /// <param name="root">The parsed line, nested at most <see cref="WireFormat.MaxDepth"/> deep</param>
    /// <returns><c>null</c> when every key is fine, otherwise the reason: <c>key not ASCII</c> or <c>duplicate key</c></returns>
    internal string? FindViolation(JsonElement root)
    {
        return Walk(root, 0);
    }

    /// <summary>
    /// Checks one key and records it.
    /// </summary>
    /// <param name="property">The property</param>
    /// <param name="set">The keys the open object added</param>
    /// <param name="keys">The same keys, in order</param>
    /// <returns>The violation, or <c>null</c></returns>
    private static string? CheckKey(JsonProperty property, HashSet<string> set, List<string> keys)
    {
        string name;

        try
        {
            name = property.Name;
        }
        catch (InvalidOperationException)
        {
            return NotAscii;
        }

        if (Ascii.IsValid(name))
        {
            if (set.Add(name))
            {
                keys.Add(name);

                return null;
            }

            return Duplicate;
        }

        return NotAscii;
    }

    /// <summary>
    /// Walks one value and everything below it.
    /// </summary>
    /// <param name="element">The value</param>
    /// <param name="depth">The zero-based nesting depth of the value</param>
    /// <returns>The first violation, or <c>null</c></returns>
    private string? Walk(JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return WalkObject(element, depth);
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var violation = Walk(item, depth + 1);

                    if (violation is not null)
                    {
                        return violation;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Walks one object: checks its keys against the keys it added and then walks the values.
    /// </summary>
    /// <param name="element">The object</param>
    /// <param name="depth">The zero-based nesting depth of the object</param>
    /// <returns>The first violation, or <c>null</c></returns>
    private string? WalkObject(JsonElement element, int depth)
    {
        var set = _sets[depth] ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = _keys[depth] ??= [];
        var count = element.GetPropertyCount();

        set.EnsureCapacity(count);
        keys.EnsureCapacity(count);

        try
        {
            foreach (var property in element.EnumerateObject())
            {
                var violation = CheckKey(property, set, keys) ?? Walk(property.Value, depth + 1);

                if (violation is not null)
                {
                    return violation;
                }
            }

            return null;
        }
        finally
        {
            Release(depth, set, keys, count);
        }
    }

    /// <summary>
    /// Removes exactly the keys the object added, or drops the depth's containers after a large object.
    /// </summary>
    /// <param name="depth">The depth of the object that ended</param>
    /// <param name="set">The set of the depth</param>
    /// <param name="keys">The keys the object added</param>
    /// <param name="count">The property count of the object</param>
    private void Release(int depth, HashSet<string> set, List<string> keys, int count)
    {
        if (count > KeptKeys)
        {
            _sets[depth] = null;
            _keys[depth] = null;

            return;
        }

        foreach (var key in keys)
        {
            set.Remove(key);
        }

        keys.Clear();
    }

    #endregion // Methods
}