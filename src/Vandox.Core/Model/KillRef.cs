using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Identifies one out-of-memory kill.
/// </summary>
public sealed class KillRef
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the boot the kill happened in.
    /// </summary>
    [JsonPropertyName("boot_id")]
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kernel clock reading of the kill in nanoseconds.
    /// </summary>
    [JsonPropertyName("kernel_time_ns")]
    public long KernelTimeNs { get; set; }

    /// <summary>
    /// Gets or sets the process ID of the victim.
    /// </summary>
    [JsonPropertyName("victim_pid")]
    public int VictimPid { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the details.
    /// </summary>
    /// <param name="path">The field path of the kill in the record</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        var error = Check.Uuid($"{path}.boot_id", BootId);

        if (error is not null)
        {
            return error;
        }

        if (KernelTimeNs < 0)
        {
            return Check.Invalid($"{path}.kernel_time_ns", "must not be negative");
        }

        return VictimPid <= 0 ? Check.Invalid($"{path}.victim_pid", "must be greater than 0") : null;
    }

    #endregion // Methods
}