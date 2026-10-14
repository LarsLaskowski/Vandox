using Microsoft.AspNetCore.Authentication;

namespace Vandox.Backend.Security;

/// <summary>
/// A session kept by the <see cref="SessionStore"/>.
/// </summary>
internal sealed class StoredSession
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="StoredSession"/> class.
    /// </summary>
    /// <param name="ticket">The ticket</param>
    /// <param name="created">The time of the sign-in</param>
    public StoredSession(AuthenticationTicket ticket, DateTimeOffset created)
    {
        Ticket = ticket;
        Created = created;
        LastActivity = created;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets or sets the ticket.
    /// </summary>
    public AuthenticationTicket Ticket { get; set; }

    /// <summary>
    /// Gets the time of the sign-in.
    /// </summary>
    public DateTimeOffset Created { get; }

    /// <summary>
    /// Gets or sets the time of the last activity.
    /// </summary>
    public DateTimeOffset LastActivity { get; set; }

    #endregion // Properties
}