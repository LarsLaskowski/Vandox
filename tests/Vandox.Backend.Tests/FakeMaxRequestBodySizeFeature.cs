using Microsoft.AspNetCore.Http.Features;

namespace Vandox.Backend.Tests;

/// <summary>
/// The request body size feature of a test request: the limit Kestrel would enforce as a backstop.
/// </summary>
internal sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
{
    #region IHttpMaxRequestBodySizeFeature

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public long? MaxRequestBodySize { get; set; } = 30_000_000;

    #endregion // IHttpMaxRequestBodySizeFeature
}