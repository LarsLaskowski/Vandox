using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

#pragma warning disable RH2003 // Skeleton: removed with the implementation
/// <summary>
/// Builds the tables of the section Services from the availability and the restarts.
/// </summary>
public static class ServicesTableView
{
    #region Methods

    /// <summary>
    /// Builds the tables and notes of the section.
    /// </summary>
    /// <param name="availability">The availability of the units</param>
    /// <param name="restarts">The restarts of the range</param>
    /// <param name="range">The range, its end already clamped to now</param>
    /// <returns>The section</returns>
    public static ServicesTable Build(ServiceAvailabilityResult availability, SeriesMarkerList restarts, HistoryRange range)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003 // Skeleton: removed with the implementation