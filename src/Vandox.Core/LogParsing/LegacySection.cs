namespace Vandox.Core.LogParsing;

/// <summary>
/// A section of a snapshot of the new format.
/// </summary>
internal enum LegacySection
{
    /// <summary>
    /// No section has begun.
    /// </summary>
    None,

    /// <summary>
    /// An unknown or repeated section whose lines are not read.
    /// </summary>
    Ignored,

    /// <summary>
    /// The lines of /proc/meminfo.
    /// </summary>
    Meminfo,

    /// <summary>
    /// The lines of the pressure stall files.
    /// </summary>
    Pressure,

    /// <summary>
    /// The lines of /proc/vmstat.
    /// </summary>
    Vmstat,

    /// <summary>
    /// The output of lsof.
    /// </summary>
    Lsof
}