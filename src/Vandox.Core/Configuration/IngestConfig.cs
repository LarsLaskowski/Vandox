using System.Globalization;

namespace Vandox.Core.Configuration;

/// <summary>
/// Configures the ingest listener and the agent it accepts.
/// </summary>
public sealed class IngestConfig
{
    #region Constants

    private const int MaxSizeDigits = 5;
    private const int MaxSizeTextLength = 8;
    private const long MaxMinFreeBytes = 1024L << 30;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the address as <c>[host]:port</c>; the host is empty or an IP address.
    /// </summary>
    [ConfigKey("listen")]
    public string Listen { get; set; } = ":8081";

    /// <summary>
    /// Gets or sets the agent ID the ingest token is bound to; <c>null</c> while unset.
    /// </summary>
    [ConfigKey("agent_id")]
    public string? AgentId { get; set; }

    /// <summary>
    /// Gets or sets the free space of the storage volume below which ingest pauses, such as <c>1GiB</c>.
    /// </summary>
    [ConfigKey("min_free")]
    public string MinFree { get; set; } = "1GiB";

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Converts a size such as <c>512MiB</c> or <c>2GiB</c>, a whole number of MiB or GiB from 0MiB to 1024GiB, to bytes.
    /// The number is one to five ASCII digits without a leading zero (a lone 0 is allowed), followed directly by the unit.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="bytes">The size in bytes; zero when the text is not valid</param>
    /// <returns><c>true</c> when the text is a size in range</returns>
    public static bool TryParseMinFree(string text, out long bytes)
    {
        bytes = 0;

        if (text.Length is < 4 or > MaxSizeTextLength)
        {
            return false;
        }

        var digits = text[..^3];
        var unit = text[^3..];
        var shift = unit switch
                    {
                        "MiB" => 20,
                        "GiB" => 30,
                        _ => 0
                    };

        if (shift == 0 || digits.Length > MaxSizeDigits || (digits.Length > 1 && digits[0] == '0'))
        {
            return false;
        }

        if (digits.All(char.IsAsciiDigit))
        {
            var size = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) << shift;

            if (size <= MaxMinFreeBytes)
            {
                bytes = size;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see cref="MinFree"/> in bytes.
    /// </summary>
    /// <returns>The number of bytes; zero switches the check off</returns>
    /// <exception cref="FormatException"><see cref="MinFree"/> is not a valid size; the loader refuses such a value</exception>
    public long GetMinFreeBytes()
    {
        if (TryParseMinFree(MinFree, out var bytes))
        {
            return bytes;
        }

        throw new FormatException("ingest.min_free is not a valid size");
    }

    #endregion // Methods
}