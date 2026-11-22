using Microsoft.AspNetCore.Components;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The range controls of the history view and of the processes view: the preset buttons and the fields <c>From (UTC)</c> and <c>To (UTC)</c> with <c>Apply</c>.
/// </summary>
public sealed partial class HistoryRangeControls : ComponentBase
{
    #region Fields

    /// <summary>
    /// The presets in the order of the buttons.
    /// </summary>
    private static readonly HistoryPreset[] _presets = [HistoryPreset.OneHour, HistoryPreset.OneDay, HistoryPreset.SevenDays, HistoryPreset.ThirtyDays, HistoryPreset.OneYear];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the preset of the address, or <see langword="null"/> when the address has none.
    /// </summary>
    [Parameter]
    public HistoryPreset? Preset { get; set; }

    /// <summary>
    /// Gets or sets whether the address has no fixed range, so that the default preset is the current one.
    /// </summary>
    [Parameter]
    public bool Unbounded { get; set; }

    /// <summary>
    /// Gets or sets the text of the start field.
    /// </summary>
    [Parameter]
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the callback that receives the text of the start field when it changes.
    /// </summary>
    [Parameter]
    public EventCallback<string> FromChanged { get; set; }

    /// <summary>
    /// Gets or sets the text of the end field.
    /// </summary>
    [Parameter]
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the callback that receives the text of the end field when it changes.
    /// </summary>
    [Parameter]
    public EventCallback<string> ToChanged { get; set; }

    /// <summary>
    /// Gets or sets the callback that receives a pressed preset.
    /// </summary>
    [Parameter]
    public EventCallback<HistoryPreset> PresetChosen { get; set; }

    /// <summary>
    /// Gets or sets the callback of the button <c>Apply</c>.
    /// </summary>
    [Parameter]
    public EventCallback ApplyPressed { get; set; }

    /// <summary>
    /// Gets the presets in the order of the buttons.
    /// </summary>
    private static IReadOnlyList<HistoryPreset> Presets => _presets;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads the texts of the fields as the range of an address, with the rules and errors of the history address.
    /// </summary>
    /// <param name="from">The text of the start field</param>
    /// <param name="to">The text of the end field</param>
    /// <returns>The address; its <see cref="HistoryAddress.Error"/> names the first error</returns>
    internal static HistoryAddress ParseFields(string from, string to)
    {
        return HistoryAddress.Parse($"?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");
    }

    /// <summary>
    /// Gives the class of the button of a preset.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <returns>The class, <see langword="null"/> for none</returns>
    private string? PresetClass(HistoryPreset preset)
    {
        var current = Preset ?? (Unbounded ? HistoryPreset.OneDay : (HistoryPreset?)null);

        return current == preset ? "active" : null;
    }

    #endregion // Methods
}