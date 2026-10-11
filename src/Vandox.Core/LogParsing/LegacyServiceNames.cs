namespace Vandox.Core.LogParsing;

#pragma warning disable RH2003, S2325

/// <summary>
/// The service names of the services file of Ubuntu's netbase with their ports.
/// </summary>
internal static class LegacyServiceNames
{
    #region Methods

    /// <summary>
    /// Looks up the port of a service name.
    /// </summary>
    /// <param name="name">The service name</param>
    /// <param name="udp"><c>true</c> for the udp protocol, <c>false</c> for tcp</param>
    /// <param name="port">The port when found</param>
    /// <returns><c>true</c> when the name is known for the protocol</returns>
    internal static bool TryGetPort(string name, bool udp, out int port)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}