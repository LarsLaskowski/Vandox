using Microsoft.AspNetCore.Http.Features;

namespace Vandox.Backend.Tests;

/// <summary>
/// A response feature that reports the response as started once bytes were written to the body, as the server does with the first flush.
/// </summary>
internal sealed class StartingResponseFeature : HttpResponseFeature
{
    #region Properties

    /// <summary>
    /// Gets or sets a value indicating whether bytes were written.
    /// </summary>
    internal bool Started { get; set; }

    #endregion // Properties

    #region HttpResponseFeature

    /// <inheritdoc />
    public override bool HasStarted => Started;

    #endregion // HttpResponseFeature
}