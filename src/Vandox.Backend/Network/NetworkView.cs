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

    /// <summary>
    /// The note on the counts of the remote addresses.
    /// </summary>
    public const string AddressNoteText = "Each snapshot counts only its 25 most frequent remote addresses and the rollups keep only the most frequent ones; an address outside them counts 0 there.";

    /// <summary>
    /// The note on the sockets per program.
    /// </summary>
    public const string ProgramNoteText = "Each process counts every socket it holds, so a socket shared by several processes (a server's master and its workers) counts once for each, and the stack can exceed the sockets per state; sockets without an owner (such as TIME_WAIT) are not in this chart.";

    /// <summary>
    /// The label of the programs the chart does not name.
    /// </summary>
    public const string OtherPrograms = "Other programs";

    /// <summary>
    /// The axis of both charts.
    /// </summary>
    public const string AxisText = "Sockets";

    /// <summary>
    /// The format of the line of the rows beyond the table of the addresses.
    /// </summary>
    public const string MoreAddressesFormat = "and {0} more addresses";

    /// <summary>
    /// The format of the line of the rows beyond the table of the listeners.
    /// </summary>
    public const string MoreListenersFormat = "and {0} more listeners";

    /// <summary>
    /// The end of the summary of a comparison of the privileged ports only.
    /// </summary>
    public const string PrivilegedOnlySuffix = "(ports 1 to 1023 only)";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds the link to the network view of an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The link</returns>
    public static string NetworkHref(NetworkAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return "network" + address.ToQuery();
    }

    #endregion // Methods
}