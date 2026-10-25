namespace Vandox.Backend.Ingest;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Reads the free space of the volume that holds the storage directory.
/// </summary>
internal sealed class FreeSpace
{
    #region Fields

    /// <summary>
    /// The storage directory.
    /// </summary>
    private readonly string _directory;

    /// <summary>
    /// Reads the free bytes of a directory.
    /// </summary>
    private readonly Func<string, long?> _probe;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FreeSpace"/> class.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <param name="probe">Reads the free bytes of a directory; replaced in tests</param>
    internal FreeSpace(string directory, Func<string, long?> probe)
    {
        _directory = directory;
        _probe = probe;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the free bytes of the volume of a directory through <see cref="DriveInfo"/>.
    /// </summary>
    /// <param name="directory">The directory</param>
    /// <returns>The free bytes; <c>null</c> on any failure</returns>
    internal static long? Probe(string directory)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reads the bytes available to this process on the volume of the storage directory.
    /// </summary>
    /// <returns>The free bytes; <c>null</c> when they cannot be read</returns>
    internal long? Available()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}