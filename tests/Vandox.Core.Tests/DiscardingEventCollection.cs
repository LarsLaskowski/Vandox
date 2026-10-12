using System.Collections.ObjectModel;

using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// A collection that counts the events added to it and keeps none.
/// </summary>
internal sealed class DiscardingEventCollection : Collection<DetectedEvent>
{
    #region Properties

    /// <summary>
    /// Gets the number of events added.
    /// </summary>
    internal int Added { get; private set; }

    #endregion // Properties

    #region Collection

    /// <inheritdoc />
    protected override void InsertItem(int index, DetectedEvent item)
    {
        Added++;
    }

    #endregion // Collection
}