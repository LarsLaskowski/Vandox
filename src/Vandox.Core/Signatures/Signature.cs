using System.Text.RegularExpressions;

namespace Vandox.Core.Signatures;

/// <summary>
/// A loaded signature of a known event.
/// </summary>
public sealed class Signature
{
    #region Constants

    /// <summary>
    /// The options every pattern is compiled with.
    /// </summary>
    internal const RegexOptions PatternOptions = RegexOptions.NonBacktracking | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Creates a signature.
    /// </summary>
    /// <param name="id">The signature ID</param>
    /// <param name="description">The description</param>
    /// <param name="programs">The listed programs</param>
    /// <param name="sources">The listed record sources</param>
    /// <param name="events">The listed events</param>
    /// <param name="pattern">The message pattern, or null</param>
    /// <param name="fields">The fields</param>
    internal Signature(string id, string description, IReadOnlyList<string> programs, IReadOnlyList<string> sources, IReadOnlyList<string> events, Regex? pattern, IReadOnlyList<SignatureField> fields)
    {
        Id = id;
        Description = description;
        Programs = programs;
        Sources = sources;
        Events = events;
        Pattern = pattern;
        Fields = fields;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the signature ID.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the listed programs, empty when the key is absent.
    /// </summary>
    public IReadOnlyList<string> Programs { get; }

    /// <summary>
    /// Gets the listed record sources, empty when the key is absent.
    /// </summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>
    /// Gets the listed events, empty when the key is absent.
    /// </summary>
    public IReadOnlyList<string> Events { get; }

    /// <summary>
    /// Gets the fields in document order.
    /// </summary>
    public IReadOnlyList<SignatureField> Fields { get; }

    /// <summary>
    /// Gets the message pattern, or null when the signature has none.
    /// </summary>
    internal Regex? Pattern { get; }

    #endregion // Properties
}