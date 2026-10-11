#pragma warning disable RH2003, S2325

namespace Vandox.Core.LogParsing;

/// <summary>
/// Matches configured log paths against file names.
/// </summary>
internal static class LogPath
{
    #region Methods

    /// <summary>
    /// Tells whether a file is the configured log file or a rotation of it.
    /// </summary>
    /// <param name="configuredPath">The configured path</param>
    /// <param name="fileName">The display path of the file</param>
    /// <returns><c>true</c> when the file matches</returns>
    internal static bool Matches(string configuredPath, string fileName)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}