namespace Vandox.Core.IO;

/// <summary>
/// Checks that the runtime validates gzip streams strictly. By default <see cref="System.IO.Compression.GZipStream"/> ends a
/// truncated stream without an error and does not check the trailer, so a cut log or batch would be taken for a complete one.
/// </summary>
public static class StrictGzip
{
    #region Constants

    /// <summary>
    /// The name of the runtime switch that turns strict validation on.
    /// </summary>
    public const string SwitchName = "System.IO.Compression.UseStrictValidation";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Throws unless strict validation is on.
    /// </summary>
    /// <exception cref="InvalidOperationException">The runtime does not validate gzip streams strictly</exception>
    public static void Require()
    {
        if (AppContext.TryGetSwitch(SwitchName, out var enabled) && enabled)
        {
            return;
        }

        throw new InvalidOperationException($"the runtime switch {SwitchName} must be true, or a truncated gzip stream is read as a complete one");
    }

    #endregion // Methods
}