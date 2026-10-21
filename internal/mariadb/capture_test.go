package mariadb

import (
	"bufio"
	"bytes"
	"encoding/hex"
	"os"
	"regexp"
	"strconv"
	"strings"
	"testing"
)

// The plaintext captures of the Product Manager: a real unix_socket login of vandox-agent on MariaDB 12.3.3-ubu2204,
// recorded with socat -x -v. The first holds SHOW GLOBAL STATUS, SHOW GLOBAL VARIABLES and SHOW FULL PROCESSLIST, the
// second SELECT CURRENT_USER() and the USER_PRIVILEGES query of the account check.
const (
	selectCapturePath  = "../../testdata/mariadb/select-status-variables-processlist.txt"
	accountCapturePath = "../../testdata/mariadb/account-check.txt"
)

// Statement texts as the captures hold them.
const (
	statusText       = "SHOW GLOBAL STATUS"
	variablesText    = "SHOW GLOBAL VARIABLES"
	processlistText  = "SHOW FULL PROCESSLIST"
	currentUserText  = "SELECT CURRENT_USER()"
	privilegesText   = "SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE LIKE '%vandox-agent%'"
	commandQuit      = 0x01
	commandQuery     = 0x03
	commandPing      = 0x0e
	maxFramePayload  = 0xffffff
	captureHexColumn = 49 // a line of a dump is a space and 16 hex bytes of three characters, then the text
)

// capturePacket is one packet of the protocol: its sequence number and its payload.
type capturePacket struct {
	seq     byte
	payload []byte
}

// bytes returns the packet as it goes over the wire.
func (p capturePacket) bytes() []byte { return encodePacket(p.seq, p.payload) }

// encodePacket frames a payload of less than 0xffffff bytes: three bytes of length, the sequence number, the payload.
func encodePacket(seq byte, payload []byte) []byte {
	n := len(payload)
	out := make([]byte, 4, 4+n)
	out[0], out[1], out[2], out[3] = byte(n&0xff), byte(n>>8&0xff), byte(n>>16&0xff), seq
	return append(out, payload...)
}

// splitPackets cuts a stream into packets and returns the packets, or the reason it is no sequence of whole packets.
func splitPackets(data []byte) ([]capturePacket, string) {
	var out []capturePacket
	for len(data) > 0 {
		if len(data) < 4 {
			return nil, "a header of " + strconv.Itoa(len(data)) + " bytes"
		}
		n := int(data[0]) | int(data[1])<<8 | int(data[2])<<16
		if n == maxFramePayload || len(data) < 4+n {
			return nil, "a packet of " + strconv.Itoa(n) + " bytes with " + strconv.Itoa(len(data)-4) + " bytes left"
		}
		out = append(out, capturePacket{seq: data[3], payload: data[4 : 4+n]})
		data = data[4+n:]
	}
	return out, ""
}

// captureTurn is what one side sent before the other answered: the bytes of the dump and the packets in them.
type captureTurn struct {
	fromServer bool
	data       []byte
	packets    []capturePacket
}

// capturedCommand is one command of the client and the server's whole answer to it.
type capturedCommand struct {
	request capturePacket
	reply   []capturePacket
}

// text returns the statement of a COM_QUERY, or "" for another command.
func (c capturedCommand) text() string {
	if len(c.request.payload) == 0 || c.request.payload[0] != commandQuery {
		return ""
	}
	return string(c.request.payload[1:])
}

// capture is a dump read into the exchange it holds: greeting, login, commands and the final COM_QUIT.
type capture struct {
	path         string
	turns        []captureTurn
	greeting     capturePacket
	loginRequest capturePacket
	loginOK      capturePacket
	commands     []capturedCommand
	quit         capturePacket
}

var dumpHeader = regexp.MustCompile(`^([<>]) \d{4}/\d\d/\d\d \d\d:\d\d:\d\d\.\d+\s+length=(\d+) from=(\d+) to=(\d+)$`)

// dumpBlock is one block of a dump: a direction and the bytes between two lines of dashes.
type dumpBlock struct {
	fromServer bool
	declared   int
	from, to   int
	data       []byte
}

// readDump reads the blocks of a dump and checks each against the length and the offsets of its header.
func readDump(t *testing.T, path string) []dumpBlock {
	t.Helper()
	file, err := os.Open(path)
	if err != nil {
		t.Fatalf("opening the capture: %v", err)
	}
	defer func() { _ = file.Close() }()
	var blocks []dumpBlock
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 0, 1<<16), 1<<20)
	for scanner.Scan() {
		line := scanner.Text()
		switch {
		case line == "--":
		case dumpHeader.MatchString(line):
			blocks = append(blocks, parseDumpHeader(t, path, line))
		case strings.HasPrefix(line, " ") && len(blocks) > 0:
			blocks[len(blocks)-1].data = append(blocks[len(blocks)-1].data, parseDumpLine(t, path, line)...)
		default:
			t.Fatalf("%s: unexpected line %q", path, line)
		}
	}
	if err := scanner.Err(); err != nil {
		t.Fatalf("reading the capture: %v", err)
	}
	checkDumpBlocks(t, path, blocks)
	return blocks
}

// parseDumpHeader reads the header line of a block.
func parseDumpHeader(t *testing.T, path, line string) dumpBlock {
	t.Helper()
	m := dumpHeader.FindStringSubmatch(line)
	nums := make([]int, 3)
	for i := range nums {
		n, err := strconv.Atoi(m[2+i])
		if err != nil {
			t.Fatalf("%s: header %q: %v", path, line, err)
		}
		nums[i] = n
	}
	return dumpBlock{fromServer: m[1] == "<", declared: nums[0], from: nums[1], to: nums[2]}
}

// parseDumpLine returns the bytes of one line of hex of a dump; the text after the hex column is ignored.
func parseDumpLine(t *testing.T, path, line string) []byte {
	t.Helper()
	region := line[1:min(len(line), captureHexColumn)]
	var out []byte
	for _, field := range strings.Fields(region) {
		b, err := hex.DecodeString(field)
		if err != nil || len(b) != 1 {
			t.Fatalf("%s: line %q holds %q, want one byte of hex", path, line, field)
		}
		out = append(out, b...)
	}
	return out
}

// checkDumpBlocks fails unless every block holds the bytes its header announces and each direction is gap-free.
func checkDumpBlocks(t *testing.T, path string, blocks []dumpBlock) {
	t.Helper()
	next := map[bool]int{}
	for i, b := range blocks {
		if len(b.data) != b.declared || b.to-b.from+1 != b.declared {
			t.Fatalf("%s: block %d holds %d bytes, header says length=%d from=%d to=%d", path, i, len(b.data), b.declared, b.from, b.to)
		}
		if b.from != next[b.fromServer] {
			t.Fatalf("%s: block %d starts at offset %d, want %d", path, i, b.from, next[b.fromServer])
		}
		next[b.fromServer] = b.to + 1
	}
}

// readCapture reads a dump and arranges its packets as the exchange of a session.
func readCapture(t *testing.T, path string) *capture {
	t.Helper()
	c := &capture{path: path}
	for _, b := range readDump(t, path) {
		if n := len(c.turns); n > 0 && c.turns[n-1].fromServer == b.fromServer {
			c.turns[n-1].data = append(c.turns[n-1].data, b.data...)
			continue
		}
		c.turns = append(c.turns, captureTurn{fromServer: b.fromServer, data: append([]byte(nil), b.data...)})
	}
	for i := range c.turns {
		packets, reason := splitPackets(c.turns[i].data)
		if reason != "" {
			t.Fatalf("%s: turn %d is no sequence of whole packets: %s", path, i, reason)
		}
		c.turns[i].packets = packets
	}
	c.arrange(t)
	return c
}

// arrange takes the packets of the turns apart: server and client alternate, the server first and the client last.
func (c *capture) arrange(t *testing.T) {
	t.Helper()
	turns := c.turns
	if len(turns) < 6 || len(turns)%2 != 0 {
		t.Fatalf("%s: %d turns, want an even number of at least 6", c.path, len(turns))
	}
	for i, turn := range turns {
		if turn.fromServer != (i%2 == 0) || (i < 3 && len(turn.packets) != 1) {
			t.Fatalf("%s: turn %d comes from the wrong side or holds %d packets", c.path, i, len(turn.packets))
		}
	}
	c.greeting, c.loginRequest, c.loginOK = turns[0].packets[0], turns[1].packets[0], turns[2].packets[0]
	if c.greeting.seq != 0 || c.loginRequest.seq != 1 || c.loginOK.seq != 2 {
		t.Fatalf("%s: the login has sequence numbers %d, %d, %d, want 0, 1, 2", c.path, c.greeting.seq, c.loginRequest.seq, c.loginOK.seq)
	}
	c.arrangeCommands(t)
}

// arrangeCommands takes the commands and the final COM_QUIT out of the turns after the login.
func (c *capture) arrangeCommands(t *testing.T) {
	t.Helper()
	turns := c.turns
	for i := 3; i+1 < len(turns); i += 2 {
		if len(turns[i].packets) != 1 || turns[i].packets[0].seq != 0 {
			t.Fatalf("%s: turn %d is no single command", c.path, i)
		}
		c.commands = append(c.commands, capturedCommand{request: turns[i].packets[0], reply: turns[i+1].packets})
	}
	last := turns[len(turns)-1].packets
	if len(last) != 1 || !bytes.Equal(last[0].payload, []byte{commandQuit}) {
		t.Fatalf("%s: the last turn is no COM_QUIT", c.path)
	}
	c.quit = last[0]
}

// command returns the captured answer to a statement.
func (c *capture) command(t *testing.T, text string) capturedCommand {
	t.Helper()
	for _, cmd := range c.commands {
		if cmd.text() == text {
			return cmd
		}
	}
	t.Fatalf("%s holds no %q", c.path, text)
	return capturedCommand{}
}

// textCell is a cell of a text result set as the tests compare it.
type textCell struct {
	value string
	null  bool
}

// decodedSet is a result set read straight from the packets of a reply, independently of the client.
type decodedSet struct {
	columns []string
	rows    [][]textCell
}

// lenenc reads a length-encoded integer; ok is false for NULL (0xfb), 0xff and a truncated one.
func lenenc(b []byte) (n uint64, rest []byte, ok bool) {
	if len(b) == 0 {
		return 0, nil, false
	}
	size := map[byte]int{0xfc: 2, 0xfd: 3, 0xfe: 8}[b[0]]
	switch {
	case b[0] < 0xfb:
		return uint64(b[0]), b[1:], true
	case size == 0 || len(b) < 1+size:
		return 0, nil, false
	}
	for i := size; i > 0; i-- {
		n = n<<8 | uint64(b[i])
	}
	return n, b[1+size:], true
}

// lenencString reads a length-encoded string.
func lenencString(b []byte) (s []byte, null bool, rest []byte, ok bool) {
	if len(b) > 0 && b[0] == 0xfb {
		return nil, true, b[1:], true
	}
	n, rest, ok := lenenc(b)
	if !ok || uint64(len(rest)) < n {
		return nil, false, nil, false
	}
	return rest[:n], false, rest[n:], true
}

// isEndPacket reports whether a payload is an EOF packet.
func isEndPacket(payload []byte) bool {
	return len(payload) > 0 && payload[0] == 0xfe && len(payload) < 9
}

// decodeResultSet reads a text result set in the captured layout: the column count and the byte that metadata
// follows, the column definitions with the extra field before the 0x0c block, an EOF, the rows and an EOF.
func decodeResultSet(t *testing.T, reply []capturePacket) decodedSet {
	t.Helper()
	var set decodedSet
	if len(reply) < 4 || len(reply[0].payload) != 2 || reply[0].payload[1] != 1 {
		t.Fatalf("a reply of %d packets is no result set in the captured layout", len(reply))
	}
	count := int(reply[0].payload[0])
	for _, p := range reply[1 : 1+count] {
		set.columns = append(set.columns, columnName(t, p.payload))
	}
	if !isEndPacket(reply[1+count].payload) || !isEndPacket(reply[len(reply)-1].payload) {
		t.Fatalf("the column definitions or the rows do not end in an EOF packet")
	}
	for _, p := range reply[2+count : len(reply)-1] {
		set.rows = append(set.rows, decodeRow(t, p.payload, count))
	}
	return set
}

// columnName returns the name of a column definition packet (its fifth string).
func columnName(t *testing.T, def []byte) string {
	t.Helper()
	var name []byte
	for i := 0; i < 5; i++ {
		var ok bool
		name, _, def, ok = lenencString(def)
		if !ok {
			t.Fatalf("unreadable column definition")
		}
	}
	return string(name)
}

// decodeRow reads the cells of a row packet.
func decodeRow(t *testing.T, payload []byte, count int) []textCell {
	t.Helper()
	cells := make([]textCell, 0, count)
	for i := 0; i < count; i++ {
		s, null, rest, ok := lenencString(payload)
		if !ok {
			t.Fatalf("unreadable row")
		}
		cells = append(cells, textCell{value: string(s), null: null})
		payload = rest
	}
	if len(payload) != 0 {
		t.Fatalf("a row has %d bytes after its last cell", len(payload))
	}
	return cells
}

// loadSelectCapture and loadAccountCapture read the two captures.
func loadSelectCapture(t *testing.T) *capture  { t.Helper(); return readCapture(t, selectCapturePath) }
func loadAccountCapture(t *testing.T) *capture { t.Helper(); return readCapture(t, accountCapturePath) }
