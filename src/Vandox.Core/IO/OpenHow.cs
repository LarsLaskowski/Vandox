using System.Runtime.InteropServices;

namespace Vandox.Core.IO;

/// <summary>
/// The parameters of <c>openat2</c> (<c>struct open_how</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OpenHow
{
    #region Fields

    /// <summary>
    /// The open flags.
    /// </summary>
    public ulong Flags;

    /// <summary>
    /// The file mode when a file is created; 0.
    /// </summary>
    public ulong Mode;

    /// <summary>
    /// The path resolution restrictions.
    /// </summary>
    public ulong Resolve;

    #endregion // Fields
}