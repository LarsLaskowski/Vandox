namespace Vandox.Backend.Network;

/// <summary>
/// The texts and limits of the network view.
/// </summary>
public static class NetworkView
{
    #region Constants

    /// <summary>
    /// The most remote addresses of the table.
    /// </summary>
    public const int AddressRows = 20;

    /// <summary>
    /// The most listener rows of the panel.
    /// </summary>
    public const int ListenerRows = 200;

    /// <summary>
    /// The title of the chart of the connection states.
    /// </summary>
    public const string StatesTitle = "Connections per state";

    /// <summary>
    /// The title of the chart of the sockets per program.
    /// </summary>
    public const string ProgramsTitle = "Sockets held per program";

    /// <summary>
    /// The heading of the table of the remote addresses.
    /// </summary>
    public const string AddressesTitle = "Top remote addresses";

    /// <summary>
    /// The heading of the panel of the listeners.
    /// </summary>
    public const string ListenersTitle = "Listeners";

    /// <summary>
    /// The text for a range without connection data.
    /// </summary>
    public const string NoDataText = "No connection data is stored in this range.";

    /// <summary>
    /// The text for a range that starts before the oldest stored connection data of the source.
    /// </summary>
    public const string StartsBeforeText = "This range starts before the oldest stored connection data of this source.";

    /// <summary>
    /// The note on snapshots with incomplete aggregates.
    /// </summary>
    public const string IncompleteText = "Some snapshots in this range may not cover every socket.";

    /// <summary>
    /// The text of a panel without a snapshot near a moment.
    /// </summary>
    public const string NoSnapshotAtText = "No connection snapshot was stored within 10 minutes of this moment.";

    /// <summary>
    /// The text of a panel without a snapshot in the range.
    /// </summary>
    public const string NoSnapshotInRangeText = "No connection snapshot of this source is stored in this range.";

    /// <summary>
    /// The text of a panel without a comparison snapshot.
    /// </summary>
    public const string NoComparisonText = "No connection snapshot of this source is stored from 24 to 48 hours before, so no listener is marked new.";

    /// <summary>
    /// The text of inventories that cannot be compared.
    /// </summary>
    public const string NotComparableText = "No listener is marked new: the inventories are not comparable.";

    /// <summary>
    /// The text of an inventory cut inside its privileged ports.
    /// </summary>
    public const string PrivilegedIncompleteText = "No listener is marked new: ports 1 to 1023 are not complete.";

    /// <summary>
    /// The text of a snapshot without an inventory state.
    /// </summary>
    public const string NotComparedText = "Listeners of imported or older snapshots are not compared.";

    /// <summary>
    /// The note on a snapshot with only the privileged ports.
    /// </summary>
    public const string PrivilegedOnlyText = "This snapshot lists only the listeners on ports 1 to 1023.";

    /// <summary>
    /// The text of a read past the deadline.
    /// </summary>
    public const string DeadlineText = "Reading the connections took longer than 10 seconds and was stopped. Narrow the range.";

    /// <summary>
    /// The text of a failed read.
    /// </summary>
    public const string FailedText = "The connections could not be read. The service log has the details.";

    /// <summary>
    /// The text of the panel until the charts chose a source.
    /// </summary>
    public const string LoadingText = "Loading…";

    /// <summary>
    /// The text of the panel when no source has data.
    /// </summary>
    public const string NoSourceText = "No source has connection data in this range, so no snapshot is shown.";

    /// <summary>
    /// The text of the panel when the chart read failed.
    /// </summary>
    public const string SourceFailedText = "The source could not be read, so no snapshot is shown.";

    /// <summary>
    /// The note on a bucket whose addresses were cut by its cap.
    /// </summary>
    public const string CapText = "Some periods in this range held more remote addresses than the rollups keep, so an address can read lower there and the count of further addresses is a lower bound.";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds the link to the network view of an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The link</returns>
    public static string NetworkHref(NetworkAddress address)
    {
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // Methods
}