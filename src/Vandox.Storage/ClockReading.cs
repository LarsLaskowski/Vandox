namespace Vandox.Storage;

/// <summary>
/// A clock reading of an agent: the bound of a counted sample with its raw capture time and its receive time.
/// </summary>
/// <param name="BoundNs">The lower bound of agent clock minus backend clock in nanoseconds</param>
/// <param name="CapturedAt">The raw capture time on the agent's clock</param>
/// <param name="ReceivedAt">The receive time on the backend's clock</param>
public readonly record struct ClockReading(long BoundNs, DateTimeOffset CapturedAt, DateTimeOffset ReceivedAt);