using System.Text.RegularExpressions;

using Vandox.Core.Model;

namespace Vandox.Core.Wire;

/// <summary>
/// Constants and rules of the versioned, compressed batch format the agent sends to the backend.
/// </summary>
public static partial class WireFormat
{
    #region Constants

    /// <summary>
    /// Major version of the format this build reads.
    /// </summary>
    public const int MajorVersion = 1;

    /// <summary>
    /// Minor version of the format this build knows.
    /// </summary>
    public const int MinorVersion = 0;

    /// <summary>
    /// Mode of a batch that carries current records.
    /// </summary>
    public const string ModeLive = "live";

    /// <summary>
    /// Mode of a batch that carries backfilled records.
    /// </summary>
    public const string ModeBackfill = "backfill";

    private const int MaxAgentIdBytes = 64;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks an agent ID: 1 to 64 characters of <c>[A-Za-z0-9._-]</c>, starting with a letter or digit.
    /// </summary>
    /// <param name="id">The agent ID</param>
    /// <returns>An error for the field <c>agent_id</c>, or <c>null</c></returns>
    public static FieldError? ValidateAgentId(string id)
    {
        if (id.Length > 0 && id.Length <= MaxAgentIdBytes && AgentIdPattern().IsMatch(id))
        {
            return null;
        }

        return new FieldError("agent_id", "must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit");
    }

    /// <summary>
    /// Tells whether the text is a lower-case UUID.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text is a lower-case UUID</returns>
    internal static bool IsBootId(string text)
    {
        return BootIdPattern().IsMatch(text);
    }

    /// <summary>
    /// Creates the pattern of an agent ID.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdPattern();

    /// <summary>
    /// Creates the pattern of a boot ID.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BootIdPattern();

    #endregion // Methods
}