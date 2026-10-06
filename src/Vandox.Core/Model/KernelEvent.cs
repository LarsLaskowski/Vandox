using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// An event reported by the kernel.
/// </summary>
public sealed class KernelEvent : IPayload
{
    #region Constants

    /// <summary>
    /// Type of an event about a process killed by the out-of-memory killer.
    /// </summary>
    public const string TypeOomKill = "oom_kill";

    /// <summary>
    /// Type of an event about a system boot.
    /// </summary>
    public const string TypeBoot = "boot";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the type of the event.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the details of an OOM kill.
    /// </summary>
    [JsonPropertyName("oom_kill")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OomKill? OomKill { get; set; }

    /// <summary>
    /// Gets or sets the details of a boot.
    /// </summary>
    [JsonPropertyName("boot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Boot? Boot { get; set; }

    /// <summary>
    /// Gets or sets the kernel message.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Message { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the details of an OOM kill event.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateOomKill()
    {
        if (OomKill is null)
        {
            return Check.Invalid("oom_kill", "required for type oom_kill");
        }

        if (Boot is not null)
        {
            return Check.Invalid("boot", "not allowed for type oom_kill");
        }

        return OomKill.Validate();
    }

    /// <summary>
    /// Checks the details of a boot event.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateBoot()
    {
        if (Boot is null)
        {
            return Check.Invalid("boot", "required for type boot");
        }

        if (OomKill is not null)
        {
            return Check.Invalid("oom_kill", "not allowed for type boot");
        }

        return Boot.Validate();
    }

    #endregion // Methods

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.KernelEvent;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.OneOf("type", Type, TypeOomKill, TypeBoot);

        if (error is not null)
        {
            return error;
        }

        error = Type == TypeOomKill ? ValidateOomKill() : ValidateBoot();

        return error ?? Check.Text("message", Message);
    }

    #endregion // IPayload
}