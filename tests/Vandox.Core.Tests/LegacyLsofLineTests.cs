using System.Net;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="LegacyLsofLine"/>
/// </summary>
[TestClass]
public class LegacyLsofLineTests
{
    #region Constants

    private const string IPv4 = "IPv4";
    private const string IPv6 = "IPv6";
    private const string Tcp = "TCP";
    private const string Udp = "UDP";
    private const string Listen = " (LISTEN)";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A TCP or UDP socket line gives the command, the process, the protocol, both endpoints and the state.
    /// </summary>
    /// <param name="type">The TYPE column</param>
    /// <param name="node">The NODE column</param>
    /// <param name="name">The NAME column with the state</param>
    /// <param name="proto">The expected protocol</param>
    /// <param name="local">The expected local endpoint</param>
    /// <param name="remote">The expected remote endpoint, or an empty text for none</param>
    /// <param name="state">The expected state</param>
    [TestMethod]
    [DataRow(IPv4, Tcp, "*:ssh (LISTEN)", "tcp", "0.0.0.0:22", "", "LISTEN")]
    [DataRow(IPv4, Tcp, "192.0.2.10:ssh->198.51.100.7:51234 (ESTABLISHED)", "tcp", "192.0.2.10:22", "198.51.100.7:51234", "ESTABLISHED")]
    [DataRow(IPv6, Tcp, "[2001:db8::1]:https->[2001:db8::2]:51234 (ESTABLISHED)", "tcp6", "[2001:db8::1]:443", "[2001:db8::2]:51234", "ESTABLISHED")]
    [DataRow(IPv4, Udp, "127.0.0.53:domain", "udp", "127.0.0.53:53", "", "")]
    [DataRow(IPv6, Udp, "[::1]:domain", "udp6", "[::1]:53", "", "")]
    [DataRow(IPv4, Udp, "192.0.2.1:5000->198.51.100.2:53", "udp", "192.0.2.1:5000", "198.51.100.2:53", "")]
    [DataRow(IPv4, Udp, "192.0.2.1:5000 (UNEXPECTED)", "udp", "192.0.2.1:5000", "", "")]
    [DataRow(IPv4, Tcp, "192.0.2.1:8080->203.0.113.9:80 (TIME_WAIT)", "tcp", "192.0.2.1:8080", "203.0.113.9:80", "TIME_WAIT")]
    [DataRow(IPv4, Tcp, "192.0.2.1:8080->203.0.113.9:80 (SYN_SENT)", "tcp", "192.0.2.1:8080", "203.0.113.9:80", "SYN_SENT")]
    [DataRow(IPv4, Tcp, "192.0.2.1:8080->203.0.113.9:80 (CLOSE_WAIT)", "tcp", "192.0.2.1:8080", "203.0.113.9:80", "CLOSE_WAIT")]
    [DataRow(IPv4, Tcp, "192.0.2.1:8080->203.0.113.9:80 (FIN_WAIT2)", "tcp", "192.0.2.1:8080", "203.0.113.9:80", "FIN_WAIT2")]
    [DataRow(IPv4, Tcp, "255.255.255.255:65535->0.0.0.0:1 (CLOSING)", "tcp", "255.255.255.255:65535", "0.0.0.0:1", "CLOSING")]
    public void LegacyLsofLineTryParseReadsTcpAndUdp(string type, string node, string name, string proto, string local, string remote, string state)
    {
        // Arrange
        var line = $"nginx      1203 www-data    8u {type}  20130      0t0  {node} {name}";

        // Act
        var parsed = LegacyLsofLine.TryParse(line, out var reason);

        // Assert
        Assert.IsNotNull(parsed, $"the line is read, reason: {reason}");
        Assert.IsNull(reason, "no reason");
        Assert.AreEqual("nginx", parsed.Command, "the command");
        Assert.AreEqual(1203, parsed.Pid, "the process ID");
        Assert.AreEqual(proto, parsed.Proto, "the protocol");
        Assert.AreEqual(local, parsed.Local.ToString(), "the local endpoint");
        Assert.AreEqual(remote, parsed.Remote?.ToString() ?? string.Empty, "the remote endpoint");
        Assert.AreEqual(state, parsed.State, "the state");
    }

    /// <summary>
    /// The process ID bounds are 1 and 2,147,483,647.
    /// </summary>
    /// <param name="pid">The PID column</param>
    /// <param name="expected">The expected process ID</param>
    [TestMethod]
    [DataRow("1", 1)]
    [DataRow("2147483647", 2147483647)]
    public void LegacyLsofLineTryParseReadsProcessIdBounds(string pid, int expected)
    {
        // Act
        var parsed = LegacyLsofLine.TryParse($"sshd {pid} root 3u IPv4 20001 0t0 TCP *:ssh (LISTEN)", out _);

        // Assert
        Assert.IsNotNull(parsed, "the line is read");
        Assert.AreEqual(expected, parsed.Pid, "the process ID");
    }

    /// <summary>
    /// A wildcard address is the unspecified address of the family, a service name is mapped through the table of the protocol, and a wildcard port is zero.
    /// </summary>
    /// <param name="type">The TYPE column</param>
    /// <param name="node">The NODE column</param>
    /// <param name="name">The NAME column with the state</param>
    /// <param name="expected">The expected local endpoint</param>
    [TestMethod]
    [DataRow(IPv4, Tcp, "*:ssh" + Listen, "0.0.0.0:22")]
    [DataRow(IPv6, Tcp, "*:https" + Listen, "[::]:443")]
    [DataRow(IPv4, Udp, "*:*", "0.0.0.0:0")]
    [DataRow(IPv6, Udp, "*:*", "[::]:0")]
    [DataRow(IPv4, Udp, "127.0.0.53:domain", "127.0.0.53:53")]
    [DataRow(IPv4, Tcp, "*:*" + Listen, "0.0.0.0:0")]
    [DataRow(IPv4, Tcp, "*:8080" + Listen, "0.0.0.0:8080")]
    [DataRow(IPv4, Tcp, "*:0" + Listen, "0.0.0.0:0")]
    [DataRow(IPv4, Tcp, "*:65535" + Listen, "0.0.0.0:65535")]
    [DataRow(IPv4, Udp, "*:fsp", "0.0.0.0:21")]
    [DataRow(IPv4, Tcp, "*:imap2" + Listen, "0.0.0.0:143")]
    [DataRow(IPv4, Tcp, "*:submissions" + Listen, "0.0.0.0:465")]
    public void LegacyLsofLineTryParseMapsWildcardsAndServiceNames(string type, string node, string name, string expected)
    {
        // Act
        var parsed = LegacyLsofLine.TryParse($"named 700 bind 12u {type} 15000 0t0 {node} {name}", out var reason);

        // Assert
        Assert.IsNotNull(parsed, $"the line is read, reason: {reason}");
        Assert.AreEqual(expected, parsed.Local.ToString(), "the local endpoint");
    }

    /// <summary>
    /// A service name that is only listed for another protocol is not mapped.
    /// </summary>
    [TestMethod]
    public void LegacyLsofLineTryParseMapsServiceNamesByProtocol()
    {
        // Act
        var tcp = LegacyLsofLine.TryParse("named 700 bind 12u IPv4 15000 0t0 TCP *:fsp (LISTEN)", out var tcpReason);

        // Assert
        Assert.IsNull(tcp, "fsp is a UDP service only");
        Assert.AreEqual(LegacyLsofLine.NotMapped, tcpReason, "the reason");
    }

    /// <summary>
    /// IPv6 addresses are in brackets; a zone is removed and the address kept with scope zero.
    /// </summary>
    [TestMethod]
    public void LegacyLsofLineTryParseReadsIpv6Brackets()
    {
        // Act
        var pair = LegacyLsofLine.TryParse("sshd 12 root 3u IPv6 1 0t0 TCP [2001:db8::1]:https->[2001:db8::2]:51234 (ESTABLISHED)", out _);
        var zoned = LegacyLsofLine.TryParse("sshd 12 root 3u IPv6 1 0t0 TCP [fe80::1%eth0]:22 (LISTEN)", out var reason);
        var upper = LegacyLsofLine.TryParse("sshd 12 root 3u IPv6 1 0t0 TCP [2001:DB8::A]:22 (LISTEN)", out _);

        // Assert
        Assert.IsNotNull(pair, "a pair of addresses");
        Assert.AreEqual(IPAddress.Parse("2001:db8::1"), pair.Local.Address, "local address");
        Assert.AreEqual(443, pair.Local.Port, "local port");
        Assert.AreEqual(IPAddress.Parse("2001:db8::2"), pair.Remote?.Address, "remote address");
        Assert.IsNotNull(zoned, $"an address with a zone, reason: {reason}");
        Assert.AreEqual(IPAddress.Parse("fe80::1"), zoned.Local.Address, "the address of the zoned endpoint");
        Assert.AreEqual(0L, zoned.Local.Address.ScopeId, "the zone is removed");
        Assert.IsNotNull(upper, "upper-case hex digits");
        Assert.AreEqual(IPAddress.Parse("2001:db8::a"), upper.Local.Address, "upper-case address");
    }

    /// <summary>
    /// A trailing dotted quad of an IPv6 address is read when it follows the IPv4 octet rule.
    /// </summary>
    [TestMethod]
    public void LegacyLsofLineTryParseReadsIpv6WithEmbeddedIpv4()
    {
        // Act
        var parsed = LegacyLsofLine.TryParse("sshd 12 root 3u IPv6 1 0t0 TCP [::ffff:192.0.2.1]:https (LISTEN)", out var reason);

        // Assert
        Assert.IsNotNull(parsed, $"the line is read, reason: {reason}");
        Assert.AreEqual(IPAddress.Parse("::ffff:192.0.2.1"), parsed.Local.Address, "the mapped address");
        Assert.AreEqual(443, parsed.Local.Port, "the port");
    }

    /// <summary>
    /// The command escapes of lsof are decoded to bytes and read as UTF-8.
    /// </summary>
    /// <param name="column">The COMMAND column</param>
    /// <param name="expected">The expected command</param>
    [TestMethod]
    [DataRow(@"php-fpm\x20", "php-fpm ")]
    [DataRow(@"my\x20app", "my app")]
    [DataRow(@"\xe4\xb8\xad", "中")]
    [DataRow(@"a\xffb", "a�b")]
    [DataRow(@"plain\n", @"plain\n")]
    [DataRow(@"x\x4", @"x\x4")]
    public void LegacyLsofLineTryParseDecodesCommandEscapes(string column, string expected)
    {
        // Arrange
        var line = $"{column} 1601 www-data 5u IPv4 20501 0t0 UDP *:*";

        // Act
        var parsed = LegacyLsofLine.TryParse(line, out var reason);

        // Assert
        Assert.Contains("\\", column, "the escape keeps its backslash in the input");
        Assert.IsNotNull(parsed, $"the line is read, reason: {reason}");
        Assert.AreEqual(expected, parsed.Command, "the decoded command");
    }

    /// <summary>
    /// An address or port form that <see cref="IPAddress.TryParse(string, out IPAddress)"/> would accept but lsof never prints is not mapped.
    /// </summary>
    /// <param name="type">The TYPE column</param>
    /// <param name="node">The NODE column</param>
    /// <param name="name">The NAME column with the state</param>
    /// <param name="expectedReason">The expected reason</param>
    [TestMethod]
    [DataRow(IPv4, Tcp, "127.1:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "1:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "0x7f.0.0.1:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "010.0.0.1:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "01.2.3.4:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "2130706433:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "1.2.3.256:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "1.2.3.4.5:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "[1.2.3.4]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "[::1]:80" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "1.2.3.4:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[::1]:22]" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[ ::1]:22" + Listen, LegacyLsofLine.NotRead)]
    [DataRow(IPv4, Tcp, "192.0.2.1:65536" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:0x16" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:022" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:-1" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:nosuchservice" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:22", LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:22 (UNKNOWN)", LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:22->1.2.3.256:80 (ESTABLISHED)", LegacyLsofLine.NotMapped)]
    [DataRow(IPv4, Tcp, "192.0.2.1:22->127.1:80 (ESTABLISHED)", LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[::ffff:1.2.3.04]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[::1.2.3.077]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[::ffff:1.2.3.256]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[::ffff:1.2.3]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[1.2.3.4::1]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[1:2:3]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[zz::1]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "[1]:22" + Listen, LegacyLsofLine.NotMapped)]
    [DataRow(IPv6, Tcp, "::1:22" + Listen, LegacyLsofLine.NotMapped)]
    public void LegacyLsofLineTryParseRefusesLenientAddressForms(string type, string node, string name, string expectedReason)
    {
        // Act
        var parsed = LegacyLsofLine.TryParse($"sshd 12 root 3u {type} 1 0t0 {node} {name}", out var reason);

        // Assert
        Assert.IsNull(parsed, "the line is not read");
        Assert.AreEqual(expectedReason, reason, "the reason");
    }

    /// <summary>
    /// A line of the wrong shape is not read: too few tokens, no IPv4 or IPv6 type, a node that is not TCP or UDP, or an invalid process ID.
    /// </summary>
    /// <param name="line">The line</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("sshd")]
    [DataRow("sshd 12 root 3u IPv4 1 0t0 TCP")]
    [DataRow("sshd 12 root 3u unix 0x1 0t0 STREAM /run/x")]
    [DataRow("sshd 12 root 3u IPv4 1 0t0 ICMP *:ssh (LISTEN)")]
    [DataRow("sshd abc root 3u IPv4 1 0t0 TCP *:ssh (LISTEN)")]
    [DataRow("sshd 0 root 3u IPv4 1 0t0 TCP *:ssh (LISTEN)")]
    [DataRow("sshd -1 root 3u IPv4 1 0t0 TCP *:ssh (LISTEN)")]
    [DataRow("sshd 2147483648 root 3u IPv4 1 0t0 TCP *:ssh (LISTEN)")]
    [DataRow("sshd 12345678901 root 3u IPv4 1 0t0 TCP *:ssh (LISTEN)")]
    [DataRow("sshd 12 root 3u IPv4 1 0t0 UDP")]
    public void LegacyLsofLineTryParseRefusesWrongShape(string line)
    {
        // Act
        var parsed = LegacyLsofLine.TryParse(line, out var reason);

        // Assert
        Assert.IsNull(parsed, "the line is not read");
        Assert.AreEqual(LegacyLsofLine.NotRead, reason, "the reason");
    }

    /// <summary>
    /// The socket key is the text of the line from the TYPE column on, so a socket shared by two processes has one key.
    /// </summary>
    [TestMethod]
    public void LegacyLsofLineTryParseBuildsSocketKeyFromTypeOn()
    {
        // Act
        var first = LegacyLsofLine.TryParse("nginx 200 root 6u IPv4 1003 0t0 TCP 192.0.2.10:https->198.51.100.7:50001 (ESTABLISHED)", out _);
        var shared = LegacyLsofLine.TryParse("worker  201 www-data 11u IPv4 1003 0t0 TCP 192.0.2.10:https->198.51.100.7:50001 (ESTABLISHED)", out _);
        var otherDevice = LegacyLsofLine.TryParse("nginx 200 root 6u IPv4 1004 0t0 TCP 192.0.2.10:https->198.51.100.7:50001 (ESTABLISHED)", out _);

        // Assert
        Assert.IsNotNull(first, "first line");
        Assert.IsNotNull(shared, "shared line");
        Assert.IsNotNull(otherDevice, "other device");
        Assert.AreEqual(first.SocketKey, shared.SocketKey, "the same socket seen by two processes");
        Assert.AreNotEqual(first.SocketKey, otherDevice.SocketKey, "another device is another socket");
        Assert.AreNotEqual(0, first.SocketKey.Length, "the key is not empty");
    }

    /// <summary>
    /// The header line of lsof is recognized by its column names.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="expected">Whether the line is the header</param>
    [TestMethod]
    [DataRow("COMMAND    PID     USER   FD   TYPE   DEVICE SIZE/OFF NODE NAME", true)]
    [DataRow("COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE NAME", true)]
    [DataRow("COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE", false)]
    [DataRow("COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE NAME EXTRA", false)]
    [DataRow("sshd 12 root 3u IPv4 1 0t0 TCP *:ssh (LISTEN)", false)]
    [DataRow("", false)]
    public void LegacyLsofLineIsHeaderMatchesColumnNames(string line, bool expected)
    {
        // Act
        var actual = LegacyLsofLine.IsHeader(line);

        // Assert
        Assert.AreEqual(expected, actual, "whether the line is the header");
    }

    #endregion // Methods
}