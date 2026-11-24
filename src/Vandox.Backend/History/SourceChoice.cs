using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// Chooses the source a chart shows.
/// </summary>
public static class SourceChoice
{
    #region Methods

    /// <summary>
    /// Chooses the source with the latest data.
    /// </summary>
    /// <param name="sources">The sources of the range</param>
    /// <returns>The source name, or an empty text when there is none</returns>
    public static string Latest(ProgramSourceList sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        ProgramSource? latest = null;

        foreach (var candidate in sources.Sources)
        {
            if (latest is null || candidate.LatestBucket > latest.LatestBucket)
            {
                latest = candidate;
            }
        }

        return latest?.Source ?? string.Empty;
    }

    #endregion // Methods
}