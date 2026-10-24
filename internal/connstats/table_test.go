package connstats

import (
	"encoding/binary"
	"errors"
	"fmt"
	"net/netip"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"testing/fstest"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	serverNetDir  = "../../testdata/proc/net"
	sandboxNetDir = "../../testdata/connections/sandbox"
)

var (
	tcpTable  = table{name: "tcp", proto: model.ProtoTCP, words: 1}
	tcp6Table = table{name: "tcp6", proto: model.ProtoTCP6, words: 4}
	udpTable  = table{name: "udp", proto: model.ProtoUDP, words: 1}
	udp6Table = table{name: "udp6", proto: model.ProtoUDP6, words: 4}
)

// tableLines returns the lines of a captured table without the header, as the kernel wrote them (trailing spaces kept).
func tableLines(t *testing.T, dir, name string) []string {
	t.Helper()

	data, err := os.ReadFile(filepath.Join(dir, name))
	if err != nil {
		t.Fatalf("reading the fixture %s/%s: %v", dir, name, err)
	}
	lines := strings.Split(strings.TrimSuffix(string(data), "\n"), "\n")
	if len(lines) < 2 || !strings.HasPrefix(strings.TrimSpace(lines[0]), "sl") {
		t.Fatalf("fixture %s/%s: first line %q is not a header", dir, name, lines[0])
	}
	return lines[1:]
}

// setField returns line with its field number i (0-based, split at runs of spaces) replaced by val.
func setField(t *testing.T, line string, i int, val string) string {
	t.Helper()

	fields := strings.Fields(line)
	if i >= len(fields) {
		t.Fatalf("line %q has %d fields, want more than %d", line, len(fields), i)
	}
	fields[i] = val
	return strings.Join(fields, " ")
}

func addrPort(s string) netip.AddrPort {
	return netip.MustParseAddrPort(s)
}

// sample names a socket of a captured table.
type sample struct {
	dir, file string
	table     table
	index     int
	want      socket
}

func TestParseLine_ServerSamples(t *testing.T) {
	counts := []struct {
		file  string
		table table
		lines int
	}{
		{"tcp", tcpTable, 28},
		{"tcp6", tcp6Table, 25},
		{"udp", udpTable, 7},
		{"udp6", udp6Table, 6},
	}
	for _, c := range counts {
		t.Run("every line of "+c.file+" parses", func(t *testing.T) {
			checkAllLines(t, serverNetDir, c.file, c.table, c.lines)
		})
	}

	checkSamples(t, []sample{
		{serverNetDir, "tcp", tcpTable, 0, socket{proto: model.ProtoTCP, local: addrPort("0.0.0.0:110"), remote: addrPort("0.0.0.0:0"), state: "LISTEN", inode: 16364321, uid: 0}},
		{serverNetDir, "tcp", tcpTable, 2, socket{proto: model.ProtoTCP, local: addrPort("127.0.0.1:3306"), remote: addrPort("0.0.0.0:0"), state: "LISTEN", inode: 16365476, uid: 114}},
		{serverNetDir, "tcp", tcpTable, 16, socket{proto: model.ProtoTCP, local: addrPort("192.0.2.10:443"), remote: addrPort("0.0.0.0:0"), state: "LISTEN", inode: 16364675, uid: 0}},
		// ESTABLISHED, a running timer, a non-zero send queue and a last field of -1.
		{serverNetDir, "tcp", tcpTable, 26, socket{proto: model.ProtoTCP, local: addrPort("192.0.2.10:22"), remote: addrPort("198.51.100.7:25249"), state: "ESTABLISHED", inode: 17280839, uid: 0}},
		// TIME_WAIT: inode 0 and fewer tail fields.
		{serverNetDir, "tcp", tcpTable, 27, socket{proto: model.ProtoTCP, local: addrPort("192.0.2.10:56518"), remote: addrPort("198.51.100.20:443"), state: "TIME_WAIT", inode: 0, uid: 0}},
		{serverNetDir, "tcp6", tcp6Table, 0, socket{proto: model.ProtoTCP6, local: addrPort("[::]:106"), remote: addrPort("[::]:0"), state: "LISTEN", inode: 22074, uid: 0}},
		{serverNetDir, "tcp6", tcp6Table, 7, socket{proto: model.ProtoTCP6, local: addrPort("[2001:db8::10]:53"), remote: addrPort("[::]:0"), state: "LISTEN", inode: 16364562, uid: 116}},
		{serverNetDir, "tcp6", tcp6Table, 10, socket{proto: model.ProtoTCP6, local: addrPort("[::1]:53"), remote: addrPort("[::]:0"), state: "LISTEN", inode: 16364558, uid: 116}},
		{serverNetDir, "tcp6", tcp6Table, 15, socket{proto: model.ProtoTCP6, local: addrPort("[fe80::200:5eff:fe00:5301]:53"), remote: addrPort("[::]:0"), state: "LISTEN", inode: 16364566, uid: 116}},
		// A connected UDP socket and an unconnected one; the state is dropped.
		{serverNetDir, "udp", udpTable, 0, socket{proto: model.ProtoUDP, local: addrPort("192.0.2.10:11651"), remote: addrPort("192.0.2.53:53"), state: "", inode: 16955396, uid: 30}},
		{serverNetDir, "udp", udpTable, 2, socket{proto: model.ProtoUDP, local: addrPort("192.0.2.10:53"), remote: addrPort("0.0.0.0:0"), state: "", inode: 16364553, uid: 116}},
		{serverNetDir, "udp", udpTable, 6, socket{proto: model.ProtoUDP, local: addrPort("127.0.0.53:53"), remote: addrPort("0.0.0.0:0"), state: "", inode: 14358468, uid: 102}},
		{serverNetDir, "udp6", udp6Table, 0, socket{proto: model.ProtoUDP6, local: addrPort("[::1]:53"), remote: addrPort("[::]:0"), state: "", inode: 16364556, uid: 116}},
		{serverNetDir, "udp6", udp6Table, 4, socket{proto: model.ProtoUDP6, local: addrPort("[fe80::200:5eff:fe00:5301]:53"), remote: addrPort("[::]:0"), state: "", inode: 16364564, uid: 116}},
	})

	t.Run("a link-local address carries no zone", func(t *testing.T) {
		line := tableLines(t, serverNetDir, "tcp6")[15]
		got, err := parseLine([]byte(line), tcp6Table)
		if err != nil {
			t.Fatalf("parseLine(link-local line) error = %v, want nil", err)
		}
		if zone := got.local.Addr().Zone(); zone != "" {
			t.Errorf("parseLine(link-local line) zone = %q, want none", zone)
		}
	})

	t.Run("an IPv4-mapped address stays mapped", func(t *testing.T) {
		line := tableLines(t, serverNetDir, "tcp6")[0]
		line = setField(t, line, 2, "0000000000000000FFFF00000100007F:C350")
		line = setField(t, line, 3, "01")
		got, err := parseLine([]byte(line), tcp6Table)
		if err != nil {
			t.Fatalf("parseLine(mapped remote) error = %v, want nil", err)
		}
		want := netip.AddrPortFrom(netip.MustParseAddr("::ffff:127.0.0.1"), 0xC350)
		if got.remote != want || got.remote.Addr().Is4() || !got.remote.Addr().Is4In6() {
			t.Errorf("parseLine(mapped remote).remote = %v (4in6 %v), want %v kept as an IPv6 address", got.remote, got.remote.Addr().Is4In6(), want)
		}
		if got.state != "ESTABLISHED" {
			t.Errorf("parseLine(mapped remote).state = %q, want ESTABLISHED", got.state)
		}
	})
}

// checkAllLines parses every line of a captured table and expects want lines, all of the table's protocol.
func checkAllLines(t *testing.T, dir, file string, tb table, want int) {
	t.Helper()

	lines := tableLines(t, dir, file)
	if len(lines) != want {
		t.Fatalf("fixture %s holds %d sockets, want %d", file, len(lines), want)
	}
	for i, line := range lines {
		got, err := parseLine([]byte(line), tb)
		if err != nil {
			t.Errorf("parseLine(%s line %d) error = %v, want nil", file, i, err)
			continue
		}
		if got.proto != tb.proto {
			t.Errorf("parseLine(%s line %d).proto = %q, want %q", file, i, got.proto, tb.proto)
		}
	}
}

func TestParseLine_SandboxSamples(t *testing.T) {
	counts := []struct {
		file  string
		table table
		lines int
	}{
		{"tcp", tcpTable, 41},
		{"udp", udpTable, 2},
	}
	for _, c := range counts {
		t.Run("every line of "+c.file+" parses", func(t *testing.T) {
			checkAllLines(t, sandboxNetDir, c.file, c.table, c.lines)
		})
	}

	checkSamples(t, []sample{
		{sandboxNetDir, "tcp", tcpTable, 1, socket{proto: model.ProtoTCP, local: addrPort("0.0.0.0:4444"), remote: addrPort("0.0.0.0:0"), state: "LISTEN", inode: 16082, uid: 0}},
		{sandboxNetDir, "tcp", tcpTable, 7, socket{proto: model.ProtoTCP, local: addrPort("127.0.0.1:4444"), remote: addrPort("127.0.0.1:55388"), state: "ESTABLISHED", inode: 16084, uid: 0}},
		{sandboxNetDir, "tcp", tcpTable, 38, socket{proto: model.ProtoTCP, local: addrPort("127.0.0.1:55388"), remote: addrPort("127.0.0.1:4444"), state: "ESTABLISHED", inode: 16083, uid: 0}},
		{sandboxNetDir, "tcp", tcpTable, 39, socket{proto: model.ProtoTCP, local: addrPort("127.0.0.1:46462"), remote: addrPort("127.0.0.1:44645"), state: "TIME_WAIT", inode: 0, uid: 0}},
		{sandboxNetDir, "udp", udpTable, 0, socket{proto: model.ProtoUDP, local: addrPort("127.0.0.1:36728"), remote: addrPort("127.0.0.1:53"), state: "", inode: 16081, uid: 0}},
		{sandboxNetDir, "udp", udpTable, 1, socket{proto: model.ProtoUDP, local: addrPort("0.0.0.0:5353"), remote: addrPort("0.0.0.0:0"), state: "", inode: 16080, uid: 0}},
	})
}

// checkSamples parses the named lines and compares the sockets.
func checkSamples(t *testing.T, samples []sample) {
	t.Helper()

	for _, s := range samples {
		t.Run(fmt.Sprintf("%s line %d", s.file, s.index), func(t *testing.T) {
			lines := tableLines(t, s.dir, s.file)
			got, err := parseLine([]byte(lines[s.index]), s.table)
			if err != nil {
				t.Fatalf("parseLine(%s line %d) error = %v, want nil", s.file, s.index, err)
			}
			if got != s.want {
				t.Errorf("parseLine(%s line %d) = %+v, want %+v", s.file, s.index, got, s.want)
			}
		})
	}
}

func TestParseLine_UID(t *testing.T) {
	line := tableLines(t, serverNetDir, "tcp")[1]

	t.Run("accepted", func(t *testing.T) { checkUIDAccepted(t, line) })
	t.Run("refused", func(t *testing.T) { checkUIDRefused(t, line) })
	t.Run("on every table", checkUIDOnEveryTable)
}

// uidField is the index of the uid field of a table line.
const uidField = 7

// checkUIDAccepted checks that the uid field values a real table can hold are read as printed.
func checkUIDAccepted(t *testing.T, line string) {
	t.Helper()

	accepted := []struct {
		field string
		want  uint32
	}{
		{"0", 0},
		{"65534", 65534},
		{"4294967295", 4294967295},
		{"0000000001", 1},
		{"1000", 1000},
	}
	for _, c := range accepted {
		got, err := parseLine([]byte(setField(t, line, uidField, c.field)), tcpTable)

		if err != nil {
			t.Errorf("parseLine(uid %q) error = %v, want nil", c.field, err)
		} else if got.uid != c.want {
			t.Errorf("parseLine(uid %q).uid = %d, want %d", c.field, got.uid, c.want)
		}
	}
}

// checkUIDRefused checks that any other uid field makes the line unreadable with the format reason.
func checkUIDRefused(t *testing.T, line string) {
	t.Helper()

	for _, field := range []string{"4294967296", "12345678901", "-1", "+1", "1a", "0x10", "1.5", "99999999999999999999"} {
		got, err := parseLine([]byte(setField(t, line, uidField, field)), tcpTable)

		if !errors.Is(err, errFormat) {
			t.Errorf("parseLine(uid %q) = %+v, %v, want the error %v", field, got, err, errFormat)
		}
	}
}

// checkUIDOnEveryTable checks that all four tables read the field alike.
func checkUIDOnEveryTable(t *testing.T) {
	t.Helper()

	bases := []struct {
		table table
		line  string
	}{
		{tcp6Table, tableLines(t, serverNetDir, "tcp6")[2]},
		{udpTable, tableLines(t, serverNetDir, "udp")[2]},
		{udp6Table, tableLines(t, serverNetDir, "udp6")[0]},
	}
	for _, b := range bases {
		got, err := parseLine([]byte(setField(t, b.line, uidField, "4242")), b.table)
		if err != nil || got.uid != 4242 {
			t.Errorf("parseLine(%s, uid 4242) = uid %d, %v, want 4242 and nil", b.table.name, got.uid, err)
		}
		if _, err := parseLine([]byte(setField(t, b.line, uidField, "4294967296")), b.table); !errors.Is(err, errFormat) {
			t.Errorf("parseLine(%s, uid 4294967296) error = %v, want %v", b.table.name, err, errFormat)
		}
	}
}

func TestParseLine_Rejects(t *testing.T) {
	v4 := tableLines(t, serverNetDir, "tcp")[1]
	v6 := tableLines(t, serverNetDir, "tcp6")[2]
	u4 := tableLines(t, serverNetDir, "udp")[2]
	u6 := tableLines(t, serverNetDir, "udp6")[0]
	bases := []struct {
		name  string
		table table
		line  string
	}{{"tcp", tcpTable, v4}, {"tcp6", tcp6Table, v6}, {"udp", udpTable, u4}, {"udp6", udp6Table, u6}}
	for _, b := range bases {
		if _, err := parseLine([]byte(b.line), b.table); err != nil {
			t.Fatalf("the unchanged %s line does not parse: %v (a reject case would prove nothing)", b.name, err)
		}
	}

	const (
		sl     = 0
		local  = 1
		remote = 2
		state  = 3
		inode  = 9
	)
	type reject struct {
		name  string
		table table
		line  string
	}
	mut := func(tb table, base string, field int, val string) reject {
		return reject{fmt.Sprintf("%s field %d %q", tb.name, field, val), tb, setField(t, base, field, val)}
	}
	cases := []reject{
		{"tcp empty line", tcpTable, ""},
		{"tcp blank line", tcpTable, "     "},
		{"tcp only the header", tcpTable, "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode"},
		{"tcp nine fields", tcpTable, strings.Join(strings.Fields(v4)[:9], " ")},
		{"tcp6 nine fields", tcp6Table, strings.Join(strings.Fields(v6)[:9], " ")},
		{"udp nine fields", udpTable, strings.Join(strings.Fields(u4)[:9], " ")},
		mut(tcpTable, v4, sl, "2"),
		mut(tcpTable, v4, sl, "x:"),
		mut(tcpTable, v4, sl, ":"),
		mut(tcpTable, v4, sl, "-2:"),
		mut(tcpTable, v4, local, "0000000:105E"),
		mut(tcpTable, v4, local, "000000000:105E"),
		mut(tcpTable, v4, local, "0100007f:105E"),
		mut(tcpTable, v4, local, "0000000G:105E"),
		mut(tcpTable, v4, local, "00000000:105e"),
		mut(tcpTable, v4, local, "00000000:105"),
		mut(tcpTable, v4, local, "00000000:0105E"),
		mut(tcpTable, v4, local, "00000000105E"),
		mut(tcpTable, v4, local, "00000000:"),
		mut(tcpTable, v4, local, ":105E"),
		mut(tcpTable, v4, local, "00000000:10\x005E"),
		mut(tcpTable, v4, local, "0000000ä:105E"),
		mut(tcpTable, v4, local, "00000000000000000000000000000000:105E"),
		mut(tcpTable, v4, remote, "00000000:00G0"),
		mut(tcpTable, v4, remote, "0000000:0000"),
		mut(tcpTable, v4, state, "A"),
		mut(tcpTable, v4, state, "00A"),
		mut(tcpTable, v4, state, "0a"),
		mut(tcpTable, v4, state, "00"),
		mut(tcpTable, v4, state, "0D"),
		mut(tcpTable, v4, state, "FF"),
		mut(tcpTable, v4, state, "ZZ"),
		mut(tcpTable, v4, inode, "12ab"),
		mut(tcpTable, v4, inode, "-1"),
		mut(tcpTable, v4, inode, "+5"),
		mut(tcpTable, v4, inode, "1.5"),
		mut(tcpTable, v4, inode, "123456789012345678901"),
		mut(tcpTable, v4, inode, "18446744073709551616"),
		mut(tcp6Table, v6, local, "00000000:105E"),
		mut(tcp6Table, v6, local, "0000000000000000000000000000000:105E"),
		mut(tcp6Table, v6, local, "000000000000000000000000000000000:105E"),
		mut(tcp6Table, v6, local, "b80d0120000000000000000010000000:0035"),
		mut(tcp6Table, v6, local, "B80D01200000000000000000100000G0:0035"),
		mut(tcp6Table, v6, remote, "00000000:0000"),
		mut(tcp6Table, v6, state, "0D"),
		mut(tcp6Table, v6, inode, "18446744073709551616"),
		mut(udpTable, u4, state, "ZZ"),
		mut(udpTable, u4, state, "7"),
		mut(udpTable, u4, state, "007"),
		mut(udpTable, u4, local, "0A0200C0:35"),
		mut(udpTable, u4, inode, "x"),
		mut(udp6Table, u6, state, "ZZ"),
		mut(udp6Table, u6, local, "0A0200C0:0035"),
		mut(udp6Table, u6, inode, "21474836470000000000000"),
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, err := parseLine([]byte(c.line), c.table)
			if err == nil {
				t.Errorf("parseLine(%q) = %+v, nil, want an error", c.line, got)
			}
		})
	}

	accepted := []struct {
		name  string
		table table
		line  string
		want  func(socket) bool
	}{
		{"inode with 20 digits within 64 bits", tcpTable, setField(t, v4, inode, "18446744073709551615"), func(s socket) bool { return s.inode == 18446744073709551615 }},
		{"exactly ten fields", tcpTable, strings.Join(strings.Fields(v4)[:10], " "), func(s socket) bool { return s.inode == 16364272 }},
		{"fields five to seven and nine are not interpreted", tcpTable, strings.Join(append(strings.Fields(v4)[:4], "zz", "zz", "zz", "0", "zz", "16364272"), " "), func(s socket) bool { return s.uid == 0 && s.inode == 16364272 }},
		{"runs of spaces between the fields", tcpTable, "   " + strings.Join(strings.Fields(v4), "   "), func(s socket) bool { return s.inode == 16364272 }},
		{"any two hex digits as a UDP state", udpTable, setField(t, u4, state, "FF"), func(s socket) bool { return s.state == "" }},
		{"UDP state dropped", udp6Table, u6, func(s socket) bool { return s.state == "" }},
	}
	for _, a := range accepted {
		t.Run("accepts "+a.name, func(t *testing.T) {
			got, err := parseLine([]byte(a.line), a.table)
			if err != nil || !a.want(got) {
				t.Errorf("parseLine(%q) = %+v, %v, want a socket accepted as the boundary case %q", a.line, got, err, a.name)
			}
		})
	}
}

// tcpTestEntry is one socket of a generated table: its local endpoint and its state code.
type tcpTestEntry struct {
	local netip.AddrPort
	state string
}

// endpointText writes ap as the kernel does: the address words in host byte order, a colon and the port.
func endpointText(ap netip.AddrPort) string {
	raw := ap.Addr().As16()
	words := raw[:]
	if ap.Addr().Is4() {
		raw4 := ap.Addr().As4()
		words = raw4[:]
	}
	var b strings.Builder
	for i := 0; i < len(words); i += 4 {
		fmt.Fprintf(&b, "%08X", binary.NativeEndian.Uint32(words[i:]))
	}
	return fmt.Sprintf("%s:%04X", b.String(), ap.Port())
}

// generatedTable builds the text of a table with the header of a captured one and one line per entry.
func generatedTable(entries []tcpTestEntry, zeroRemote string) string {
	var b strings.Builder
	b.WriteString("  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n")
	for i, e := range entries {
		fmt.Fprintf(&b, "%4d: %s %s %s 00000000:00000000 00:00000000 00000000     0        0 %d 1 0000000000000000 100 0 0 10 0\n",
			i, endpointText(e.local), zeroRemote, e.state, 1000+i)
	}
	return b.String()
}

const (
	zeroRemote4 = "00000000:0000"
	zeroRemote6 = "00000000000000000000000000000000:0000"
)

// collectListeners runs TCPListeners on proc and returns the visited endpoints in order.
func collectListeners(proc fstest.MapFS) ([]netip.AddrPort, error) {
	var got []netip.AddrPort
	err := TCPListeners(proc, func(local netip.AddrPort) { got = append(got, local) })
	return got, err
}

func TestTCPListeners_VisitsListeningSockets(t *testing.T) {
	loopback := addrPort("127.0.0.1:3306")
	anyV4 := addrPort("0.0.0.0:3306")
	public := addrPort("203.0.113.7:3307")
	v6any := addrPort("[::]:3306")
	v6loop := addrPort("[::1]:3306")
	mapped := addrPort("[::ffff:127.0.0.1]:3306")
	tcp4 := generatedTable([]tcpTestEntry{
		{loopback, "0A"}, {addrPort("10.0.0.5:51000"), "01"}, {anyV4, "0A"}, {addrPort("10.0.0.5:51001"), "06"}, {public, "0A"},
	}, zeroRemote4)
	tcp6 := generatedTable([]tcpTestEntry{
		{v6any, "0A"}, {addrPort("[2001:db8::1]:51000"), "01"}, {v6loop, "0A"}, {mapped, "0A"},
	}, zeroRemote6)

	t.Run("calls visit for the LISTEN sockets of tcp then tcp6 in table order", func(t *testing.T) {
		got, err := collectListeners(fstest.MapFS{
			"net/tcp":  {Data: []byte(tcp4)},
			"net/tcp6": {Data: []byte(tcp6)},
		})

		if err != nil {
			t.Fatalf("TCPListeners() error = %v, want nil", err)
		}
		want := []netip.AddrPort{loopback, anyV4, public, v6any, v6loop, mapped}
		if !slices.Equal(got, want) {
			t.Errorf("TCPListeners() visited %v, want %v", got, want)
		}
	})

	t.Run("a missing tcp6 table is skipped", func(t *testing.T) {
		got, err := collectListeners(fstest.MapFS{"net/tcp": {Data: []byte(tcp4)}})

		if err != nil {
			t.Fatalf("TCPListeners() without tcp6 error = %v, want nil", err)
		}
		want := []netip.AddrPort{loopback, anyV4, public}
		if !slices.Equal(got, want) {
			t.Errorf("TCPListeners() without tcp6 visited %v, want %v", got, want)
		}
	})

	t.Run("a missing tcp table is an error", func(t *testing.T) {
		_, err := collectListeners(fstest.MapFS{"net/tcp6": {Data: []byte(tcp6)}})

		if err == nil {
			t.Errorf("TCPListeners() without tcp error = nil, want an error")
		}
	})

	t.Run("a table without sockets visits nothing", func(t *testing.T) {
		got, err := collectListeners(fstest.MapFS{"net/tcp": {Data: []byte(generatedTable(nil, zeroRemote4))}})

		if err != nil || len(got) != 0 {
			t.Errorf("TCPListeners() = %v, %v, want no endpoint and nil", got, err)
		}
	})
}

func TestTCPListeners_UnreadableLineIsAnError(t *testing.T) {
	good4 := generatedTable([]tcpTestEntry{{addrPort("127.0.0.1:3306"), "0A"}}, zeroRemote4)
	good6 := generatedTable([]tcpTestEntry{{addrPort("[::1]:3306"), "0A"}}, zeroRemote6)
	bad := []struct {
		name string
		line string
	}{
		{"too few fields", "   1: 0100007F:0CEA 00000000:0000 0A\n"},
		{"unknown state", "   1: 0100007F:0CEA 00000000:0000 FF 00000000:00000000 00:00000000 00000000 0 0 5 1\n"},
		{"bad address", "   1: 0100007Z:0CEA 00000000:0000 0A 00000000:00000000 00:00000000 00000000 0 0 5 1\n"},
		{"a line over the limit", strings.Repeat("x", 600) + "\n"},
	}
	for _, tt := range bad {
		t.Run("tcp "+tt.name, func(t *testing.T) {
			_, err := collectListeners(fstest.MapFS{"net/tcp": {Data: []byte(good4 + tt.line)}, "net/tcp6": {Data: []byte(good6)}})

			if err == nil {
				t.Errorf("TCPListeners() with %s in tcp error = nil, want an error", tt.name)
			}
		})
		t.Run("tcp6 "+tt.name, func(t *testing.T) {
			line := strings.Replace(tt.line, "0100007F", "00000000000000000000000001000000", 1)
			_, err := collectListeners(fstest.MapFS{"net/tcp": {Data: []byte(good4)}, "net/tcp6": {Data: []byte(good6 + line)}})

			if err == nil {
				t.Errorf("TCPListeners() with %s in tcp6 error = nil, want an error", tt.name)
			}
		})
	}
}

func TestTCPListeners_CapturedServerTables(t *testing.T) {
	const wantListeners = 26 + 25 // LISTEN lines (state 0A) of the captured tcp and tcp6
	got, err := collectListeners(nil)
	if err == nil {
		t.Fatalf("TCPListeners(empty tree) = %v, nil, want an error for the missing tcp", got)
	}
	proc := fstest.MapFS{}
	for _, name := range []string{"tcp", "tcp6"} {
		data, rerr := os.ReadFile(filepath.Join(serverNetDir, name))
		if rerr != nil {
			t.Fatalf("reading the fixture %s: %v", name, rerr)
		}
		proc["net/"+name] = &fstest.MapFile{Data: data}
	}

	got, err = collectListeners(proc)

	if err != nil {
		t.Fatalf("TCPListeners(captured tables) error = %v, want nil", err)
	}
	if len(got) != wantListeners {
		t.Errorf("TCPListeners(captured tables) visited %d sockets, want %d", len(got), wantListeners)
	}
	if !slices.Contains(got, addrPort("127.0.0.1:3306")) {
		t.Errorf("TCPListeners(captured tables) = %v, want it to hold 127.0.0.1:3306", got)
	}
}

func TestTCPListeners_RefusesABadUID(t *testing.T) {
	good4 := generatedTable([]tcpTestEntry{{addrPort("127.0.0.1:3306"), "0A"}}, zeroRemote4)
	good6 := generatedTable([]tcpTestEntry{{addrPort("[::1]:3306"), "0A"}}, zeroRemote6)
	const badUID = "   1: 0100007F:0CEA 00000000:0000 0A 00000000:00000000 00:00000000 00000000 4294967296 0 5 1\n"

	t.Run("tcp", func(t *testing.T) {
		_, err := collectListeners(fstest.MapFS{"net/tcp": {Data: []byte(good4 + badUID)}, "net/tcp6": {Data: []byte(good6)}})

		if err == nil || !errors.Is(err, errFormat) {
			t.Errorf("TCPListeners() with a uid of 4294967296 in tcp error = %v, want one wrapping %v", err, errFormat)
		}
	})
	t.Run("tcp6", func(t *testing.T) {
		line := strings.Replace(badUID, "0100007F", "00000000000000000000000001000000", 1)
		_, err := collectListeners(fstest.MapFS{"net/tcp": {Data: []byte(good4)}, "net/tcp6": {Data: []byte(good6 + line)}})

		if err == nil || !errors.Is(err, errFormat) {
			t.Errorf("TCPListeners() with a uid of 4294967296 in tcp6 error = %v, want one wrapping %v", err, errFormat)
		}
	})
}
