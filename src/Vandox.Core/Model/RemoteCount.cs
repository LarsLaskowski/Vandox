using System.Net;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Counts sockets per remote address.
/// </summary>
public sealed class RemoteCount
{
    #region Properties

    /// <summary>
    /// Gets or sets the remote address.
    /// </summary>
    [JsonPropertyName("addr")]
    [JsonConverter(typeof(IpAddressJsonConverter))]
    public IPAddress? Addr { get; set; }

    /// <summary>
    /// Gets or sets the number of sockets.
    /// </summary>
    [JsonPropertyName("count")]
    public uint Count { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the entry.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        return Check.Address($"{path}.addr", Addr) ?? ConnectionSnapshot.CheckCount(path, Count);
    }

    #endregion // Methods
}