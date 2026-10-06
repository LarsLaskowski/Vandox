using System.Reflection;

namespace Vandox.Backend.Cli;

/// <summary>
/// The build information injected at build time: version, commit and build date.
/// </summary>
public static class VersionInfo
{
    #region Constants

    private const string Unknown = "unknown";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Returns a single-line description of the build of an assembly.
    /// </summary>
    /// <param name="name">The name of the binary</param>
    /// <param name="assembly">The assembly that carries the build information</param>
    /// <returns>The description, e.g. <c>vandoxd v0.1.0 (commit abc, built 2026-10-01T10:00:00Z)</c></returns>
    public static string Describe(string name, Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        var commit = Metadata(assembly, "Commit");
        var date = Metadata(assembly, "BuildDate");

        return $"{name} {version} (commit {commit}, built {date})";
    }

    /// <summary>
    /// Reads a value of the assembly metadata.
    /// </summary>
    /// <param name="assembly">The assembly</param>
    /// <param name="key">The key</param>
    /// <returns>The value, or <c>unknown</c></returns>
    private static string Metadata(Assembly assembly, string key)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;

        return string.IsNullOrEmpty(value) ? Unknown : value;
    }

    #endregion // Methods
}