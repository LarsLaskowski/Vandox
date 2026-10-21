package mariadb

import (
	"bufio"
	"bytes"
	"context"
	"encoding/binary"
	"io"
	"net"
	"sync"
	"testing"
	"time"
)

// Capability bits of the protocol the fake looks at.
const (
	capLocalFiles       uint32 = 0x00000080
	capProtocol41       uint32 = 0x00000200
	capSecureConnection uint32 = 0x00008000
	capMultiStatements  uint32 = 0x00010000
	capPluginAuth       uint32 = 0x00080000
	capConnectAttrs     uint32 = 0x00100000
	capLenencAuthData   uint32 = 0x00200000
	capSessionTrack     uint32 = 0x00800000
	capDeprecateEOF     uint32 = 0x01000000

	extProgress      uint32 = 0x01
	extBulk          uint32 = 0x04
	extMetadata      uint32 = 0x08
	extCacheMetadata uint32 = 0x10

	// The captured client's bits and the agent's: the captured ones without LOCAL_FILES, MULTI_STATEMENTS and
	// CONNECT_ATTRS, and the extended ones without progress reports and bulk operations.
	capturedCapabilities uint32 = 0x00bfa284
	capturedExtended     uint32 = 0x1d
	agentCapabilities    uint32 = 0x00aea204
	agentExtended        uint32 = 0x18

	statusMoreResults  uint16 = 0x0008
	statusAutocommit   uint16 = 0x0002
	statusCaptured     uint16 = 0x0022 // the status of the EOF packets of the captures
	serverBadHandshake uint16 = 1043
	serverUnknownCmd   uint16 = 1047
	serverSyntaxError  uint16 = 1064

	authPlugin = "mysql_native_password"
)

// greeting is the initial handshake packet of the server, taken apart so that a test can change one field.
type greeting struct {
	protocol  byte
	version   string
	id        uint32
	scramble1 []byte // 8 bytes
	caps      uint32
	tail      []byte // charset, status, scramble length and reserved bytes up to the extended capabilities
	ext       uint32
	rest      []byte // the second part of the scramble and the plugin name
}

// parseGreeting takes a greeting payload apart.
func parseGreeting(p []byte) (greeting, bool) {
	end := bytes.IndexByte(p, 0)
	if len(p) < 1 || end < 0 {
		return greeting{}, false
	}
	g := greeting{protocol: p[0], version: string(p[1:end])}
	b := p[end+1:]
	if len(b) < 4+8+1+2+1+2+2+1+6+4 {
		return greeting{}, false
	}
	g.id = binary.LittleEndian.Uint32(b)
	g.scramble1 = b[4:12]
	g.caps = uint32(binary.LittleEndian.Uint16(b[13:])) | uint32(binary.LittleEndian.Uint16(b[18:]))<<16
	g.tail = append(append([]byte(nil), b[15:18]...), b[20:27]...)
	g.ext = binary.LittleEndian.Uint32(b[27:])
	g.rest = b[31:]
	return g, true
}

// bytes puts a greeting back together; parseGreeting and bytes are inverse.
func (g greeting) bytes() []byte {
	out := append([]byte{g.protocol}, g.version...)
	out = append(out, 0)
	out = binary.LittleEndian.AppendUint32(out, g.id)
	out = append(out, g.scramble1...)
	out = append(out, 0)
	out = binary.LittleEndian.AppendUint16(out, uint16(g.caps&0xffff))
	out = append(out, g.tail[:3]...)
	out = binary.LittleEndian.AppendUint16(out, uint16(g.caps>>16&0xffff))
	out = append(out, g.tail[3:]...)
	out = binary.LittleEndian.AppendUint32(out, g.ext)
	return append(out, g.rest...)
}

// handshake is the client's handshake response, taken apart.
type handshake struct {
	seq       byte
	payload   []byte
	caps      uint32
	maxPacket uint32
	charset   byte
	filler    []byte
	ext       uint32
	user      string
	authLen   int
	auth      []byte
	plugin    string
	rest      []byte
}

// parseHandshake takes the handshake response apart, for the layout of the capabilities it announces.
func parseHandshake(p capturePacket) (handshake, bool) {
	b := p.payload
	if len(b) < 33 {
		return handshake{}, false
	}
	h := handshake{seq: p.seq, payload: b, caps: binary.LittleEndian.Uint32(b), maxPacket: binary.LittleEndian.Uint32(b[4:]), charset: b[8]}
	h.filler = b[9:28]
	h.ext = binary.LittleEndian.Uint32(b[28:])
	rest := b[32:]
	end := bytes.IndexByte(rest, 0)
	if end < 0 || len(rest) < end+2 {
		return handshake{}, false
	}
	h.user = string(rest[:end])
	rest = rest[end+1:]
	h.authLen = int(rest[0])
	if len(rest) < 1+h.authLen {
		return handshake{}, false
	}
	h.auth = rest[1 : 1+h.authLen]
	rest = rest[1+h.authLen:]
	end = bytes.IndexByte(rest, 0)
	if end < 0 {
		return handshake{}, false
	}
	h.plugin = string(rest[:end])
	h.rest = rest[end+1:]
	return h, true
}

// missingLayoutBits names what a handshake lacks of the captured client's layout bits, or has too much of: a client
// without them would get replies in a layout the captures do not hold.
func missingLayoutBits(h handshake) []string {
	var problems []string
	for _, bit := range []struct {
		name string
		set  bool
	}{
		{"CLIENT_PROTOCOL_41", h.caps&capProtocol41 != 0},
		{"CLIENT_SESSION_TRACK", h.caps&capSessionTrack != 0},
		{"CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA", h.caps&capLenencAuthData != 0},
		{"MARIADB_CLIENT_EXTENDED_METADATA", h.ext&extMetadata != 0},
		{"MARIADB_CLIENT_CACHE_METADATA", h.ext&extCacheMetadata != 0},
		{"no CLIENT_DEPRECATE_EOF", h.caps&capDeprecateEOF == 0},
	} {
		if !bit.set {
			problems = append(problems, bit.name)
		}
	}
	return problems
}

// errorPayloadFromDocs builds an error packet as the protocol documentation describes it: 0xff, the error number, and
// with a state the marker '#' and five characters, then the message.
func errorPayloadFromDocs(code uint16, state, message string) []byte {
	out := binary.LittleEndian.AppendUint16([]byte{0xff}, code)
	if state != "" {
		out = append(append(out, '#'), state...)
	}
	return append(out, message...)
}

// greetingErrorFromDocs is the error packet in place of the greeting (1040 at max_connections, 1053 during a
// shutdown): the server does not know the client's capabilities yet, so it has no state marker. Not captured.
func greetingErrorFromDocs(code uint16, message string) []capturePacket {
	return []capturePacket{{seq: 0, payload: errorPayloadFromDocs(code, "", message)}}
}

// loginErrorFromDocs is the error packet that answers the handshake response (1698 or 1045 for a refused unix_socket
// login, 1226 at the user's connection limit), with the marker of a client that announced PROTOCOL_41. Not captured.
func loginErrorFromDocs(code uint16, state, message string) []byte {
	return errorPayloadFromDocs(code, state, message)
}

// statementErrorFromDocs is the error packet that answers a statement or a ping. Not captured.
func statementErrorFromDocs(code uint16, state, message string) []byte {
	return errorPayloadFromDocs(code, state, message)
}

// pingOKFromDocs is the OK packet that answers COM_PING: the header, no affected rows, no insert id, the status of
// an autocommit session and no warnings. Not captured.
func pingOKFromDocs() []byte {
	return []byte{0x00, 0x00, 0x00, byte(statusAutocommit & 0xff), byte(statusAutocommit >> 8), 0x00, 0x00}
}

// authSwitchFromDocs is the request to switch to another authentication method: 0xfe, the plugin's name and the
// plugin's data (a 20-byte scramble and a NUL). Not captured.
func authSwitchFromDocs() []byte {
	out := append([]byte{0xfe}, "mysql_old_password"...)
	out = append(out, 0)
	return append(out, bytes.Repeat([]byte{'s'}, 20)...)
}

// authMoreDataFromDocs is an authentication continuation request (0x01 and data). Not captured.
func authMoreDataFromDocs() []byte { return []byte{0x01, 'm', 'o', 'r', 'e'} }

// eofPayload builds an EOF packet with the given status flags.
func eofPayload(status uint16) []byte {
	return []byte{0xfe, 0x00, 0x00, byte(status & 0xff), byte(status >> 8)}
}

// lenencBytes encodes a length-encoded integer in its shortest form.
func lenencBytes(n uint64) []byte {
	switch {
	case n < 0xfb:
		return []byte{byte(n)}
	case n < 1<<16:
		return []byte{0xfc, byte(n & 0xff), byte(n >> 8 & 0xff)}
	case n < 1<<24:
		return []byte{0xfd, byte(n & 0xff), byte(n >> 8 & 0xff), byte(n >> 16 & 0xff)}
	}
	return binary.LittleEndian.AppendUint64([]byte{0xfe}, n)
}

// rowPayload builds a row of a text result set: every cell length-encoded, a NULL as 0xfb.
func rowPayload(cells ...textCell) []byte {
	var out []byte
	for _, c := range cells {
		if c.null {
			out = append(out, 0xfb)
			continue
		}
		out = append(append(out, lenencBytes(uint64(len(c.value)))...), c.value...)
	}
	return out
}

// text builds a cell that holds a value.
func text(s string) textCell { return textCell{value: s} }

// nullCell is a NULL cell.
var nullCell = textCell{null: true}

// sequenced numbers payloads as the packets of a reply to a command, from 1.
func sequenced(payloads ...[]byte) []capturePacket {
	out := make([]capturePacket, len(payloads))
	for i, p := range payloads {
		out[i] = capturePacket{seq: byte((i + 1) & 0xff), payload: p}
	}
	return out
}

// resultSetPackets builds the reply of a statement in the captured layout from the column definitions of a capture:
// the column count with the byte that metadata follows, the definitions, an EOF, the rows and an EOF.
func resultSetPackets(defs [][]byte, rows [][]textCell) []capturePacket {
	payloads := [][]byte{{byte(len(defs) & 0xff), 1}}
	payloads = append(payloads, defs...)
	payloads = append(payloads, eofPayload(statusCaptured))
	for _, r := range rows {
		payloads = append(payloads, rowPayload(r...))
	}
	return sequenced(append(payloads, eofPayload(statusCaptured))...)
}

// capturedDefinitions returns the column definition packets of a statement's captured reply.
func capturedDefinitions(t *testing.T, c *capture, statement string) [][]byte {
	t.Helper()
	reply := c.command(t, statement).reply
	count := int(reply[0].payload[0])
	defs := make([][]byte, count)
	for i := range defs {
		defs[i] = reply[1+i].payload
	}
	return defs
}

// processlistRow builds a row of SHOW FULL PROCESSLIST in the captured column layout.
func processlistRow(id, user, host, db, command, seconds, state, info, progress string) []textCell {
	cells := []textCell{text(id), text(user), text(host), text(db), text(command), text(seconds), text(state), text(info), text(progress)}
	if db == "" {
		cells[3] = nullCell
	}
	if info == "" {
		cells[7] = nullCell
	}
	return cells
}

// fakeConfig tells a fake server what to do differently from the captures.
type fakeConfig struct {
	// Capture supplies the greeting and the login OK; the select capture when nil.
	Capture *capture
	// Greeting replaces the greeting by these packets; an error packet ends the connection.
	Greeting []capturePacket
	// NoGreeting makes the server accept the connection and say nothing.
	NoGreeting bool
	// SilentLogin makes the server greet and then never answer the handshake response.
	SilentLogin bool
	// LoginAnswers replace the login OK: each answer is sent at the next sequence number, and after an authentication
	// request (0xfe or 0x01) the server reads the client's response before the next answer.
	LoginAnswers [][]byte
	// Replies replace the captured answer to a statement; the key is the statement text.
	Replies map[string][]capturePacket
	// Ping replaces the answer to COM_PING.
	Ping []byte
	// OnCommand is called with "ping" or the statement text before the answer; true means the hook answered.
	OnCommand func(s *fakeSession, command string) bool
}

// fakeServer plays the MariaDB server on net.Pipe: it greets and logs in as the captures hold it and answers every
// captured statement with the captured packets, byte for byte. Replies the captures do not hold are given in the
// config (built in functions named ...FromDocs, or generated in the captured layout). It records what the client sends.
// A handshake without the layout bits of the captured client is refused with error 1043 before anything is replayed.
type fakeServer struct {
	t       testing.TB
	cfg     fakeConfig
	capture *capture
	replies map[string][]capturePacket
	wg      sync.WaitGroup

	mu         sync.Mutex
	dials      int
	open       int
	maxOpen    int
	handshakes []handshake
	received   []capturePacket
	commands   []string
	refusals   [][]string
}

// newFakeServer returns a fake server that replays the captures.
func newFakeServer(t *testing.T, cfg fakeConfig) *fakeServer {
	t.Helper()
	sel, account := loadSelectCapture(t), loadAccountCapture(t)
	s := &fakeServer{t: t, cfg: cfg, capture: cfg.Capture, replies: map[string][]capturePacket{}}
	if s.capture == nil {
		s.capture = sel
	}
	for _, c := range []*capture{sel, account} {
		for _, cmd := range c.commands {
			s.replies[cmd.text()] = cmd.reply
		}
	}
	for text, reply := range cfg.Replies {
		s.replies[text] = reply
	}
	return s
}

// dial is the Dialer of the tests: a net.Pipe whose other end the fake serves. The connection is closed when the test ends.
func (s *fakeServer) dial(context.Context) (net.Conn, error) {
	client, server := net.Pipe()
	s.mu.Lock()
	s.dials++
	s.open++
	s.maxOpen = max(s.maxOpen, s.open)
	s.mu.Unlock()
	s.wg.Add(1)
	go s.serve(server)
	s.t.Cleanup(func() {
		_ = client.Close()
		_ = server.Close()
		s.wg.Wait()
	})
	return client, nil
}

// wait returns when every served connection has ended.
func (s *fakeServer) wait() {
	s.t.Helper()
	s.wg.Wait()
}

// snapshot returns copies of what the fake recorded.
func (s *fakeServer) snapshot() (commands []string, received []capturePacket, handshakes []handshake) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]string(nil), s.commands...), append([]capturePacket(nil), s.received...), append([]handshake(nil), s.handshakes...)
}

// refused returns the layout problems of every handshake the fake refused.
func (s *fakeServer) refused() [][]string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([][]string(nil), s.refusals...)
}

// counts returns the number of dials and the highest number of connections open at once.
func (s *fakeServer) counts() (dials, maxOpen int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.dials, s.maxOpen
}

// fakeSession is one connection of a fake server.
type fakeSession struct {
	s    *fakeServer
	conn net.Conn
	r    *bufio.Reader
}

// send writes packets and reports whether the client still listens.
func (c *fakeSession) send(packets ...capturePacket) bool {
	for _, p := range packets {
		if _, err := c.conn.Write(p.bytes()); err != nil {
			return false
		}
	}
	return true
}

// read reads the next packet of the client and records it.
func (c *fakeSession) read() (capturePacket, bool) {
	var header [4]byte
	if _, err := io.ReadFull(c.r, header[:]); err != nil {
		return capturePacket{}, false
	}
	n := int(header[0]) | int(header[1])<<8 | int(header[2])<<16
	payload := make([]byte, n)
	if _, err := io.ReadFull(c.r, payload); err != nil {
		return capturePacket{}, false
	}
	p := capturePacket{seq: header[3], payload: payload}
	c.s.mu.Lock()
	c.s.received = append(c.s.received, p)
	c.s.mu.Unlock()
	return p, true
}

// hang reads and drops what the client sends until it closes the connection: the server that never answers.
func (c *fakeSession) hang() { _, _ = io.Copy(io.Discard, c.conn) }

// close closes the connection from the server's side.
func (c *fakeSession) close() { _ = c.conn.Close() }

// record notes a command the client sent.
func (s *fakeServer) record(command string) {
	s.mu.Lock()
	s.commands = append(s.commands, command)
	s.mu.Unlock()
}

// serve plays the server on one connection.
func (s *fakeServer) serve(conn net.Conn) {
	defer s.wg.Done()
	defer func() {
		_ = conn.Close()
		s.mu.Lock()
		s.open--
		s.mu.Unlock()
	}()
	c := &fakeSession{s: s, conn: conn, r: bufio.NewReader(conn)}
	if s.cfg.NoGreeting {
		c.hang()
		return
	}
	greet := s.cfg.Greeting
	if greet == nil {
		greet = []capturePacket{s.capture.greeting}
	}
	if !c.send(greet...) || (len(greet[0].payload) > 0 && greet[0].payload[0] == 0xff) {
		return
	}
	if s.login(c) {
		s.commandLoop(c)
	}
}

// login reads the handshake response and answers it; true means the client is logged in.
func (s *fakeServer) login(c *fakeSession) bool {
	p, ok := c.read()
	if !ok {
		return false
	}
	h, ok := parseHandshake(p)
	if !ok {
		c.send(capturePacket{seq: p.seq + 1, payload: errorPayloadFromDocs(serverBadHandshake, "08S01", "Bad handshake")})
		return false
	}
	s.mu.Lock()
	s.handshakes = append(s.handshakes, h)
	s.mu.Unlock()
	if s.cfg.SilentLogin {
		c.hang()
		return false
	}
	if problems := missingLayoutBits(h); len(problems) > 0 {
		s.mu.Lock()
		s.refusals = append(s.refusals, problems)
		s.mu.Unlock()
		c.send(capturePacket{seq: p.seq + 1, payload: errorPayloadFromDocs(serverBadHandshake, "08S01", "Bad handshake")})
		return false
	}
	return s.answerLogin(c, p.seq+1)
}

// answerLogin sends the login OK or the configured answers.
func (s *fakeServer) answerLogin(c *fakeSession, seq byte) bool {
	if s.cfg.LoginAnswers == nil {
		return c.send(s.capture.loginOK)
	}
	for i, answer := range s.cfg.LoginAnswers {
		if !c.send(capturePacket{seq: seq, payload: answer}) {
			return false
		}
		if len(answer) == 0 || (answer[0] != 0xfe && answer[0] != 0x01) {
			return len(answer) > 0 && answer[0] == 0x00
		}
		response, ok := c.read()
		if !ok {
			return false
		}
		seq = response.seq + 1
		if i == len(s.cfg.LoginAnswers)-1 {
			return false
		}
	}
	return false
}

// commandLoop answers the commands of a logged-in client until it quits or the connection ends.
func (s *fakeServer) commandLoop(c *fakeSession) {
	for {
		p, ok := c.read()
		if !ok || len(p.payload) == 0 {
			return
		}
		switch p.payload[0] {
		case commandQuit:
			s.record("quit")
			return
		case commandPing:
			s.record("ping")
			s.answerPing(c)
		case commandQuery:
			command := string(p.payload[1:])
			s.record(command)
			s.answerQuery(c, command)
		default:
			s.record("command")
			c.send(capturePacket{seq: 1, payload: errorPayloadFromDocs(serverUnknownCmd, "08S01", "Unknown command")})
		}
	}
}

// answerPing answers COM_PING.
func (s *fakeServer) answerPing(c *fakeSession) {
	if s.cfg.OnCommand != nil && s.cfg.OnCommand(c, "ping") {
		return
	}
	ping := s.cfg.Ping
	if ping == nil {
		ping = pingOKFromDocs()
	}
	c.send(capturePacket{seq: 1, payload: ping})
}

// answerQuery answers a statement with its captured or configured reply; another statement gets error 1064.
func (s *fakeServer) answerQuery(c *fakeSession, command string) {
	if s.cfg.OnCommand != nil && s.cfg.OnCommand(c, command) {
		return
	}
	reply, ok := s.replies[command]
	if !ok {
		reply = sequenced(errorPayloadFromDocs(serverSyntaxError, "42000", "You have an error in your SQL syntax"))
	}
	c.send(reply...)
}

// hangOn returns an OnCommand hook that never answers the given command (and answers the others as usual).
func hangOn(command string) func(*fakeSession, string) bool {
	return func(c *fakeSession, got string) bool {
		if got != command {
			return false
		}
		c.hang()
		return true
	}
}

// closeAfter returns an OnCommand hook that sends the first n packets of the statement's reply and closes the connection.
func closeAfter(reply []capturePacket, n int) func(*fakeSession, string) bool {
	return func(c *fakeSession, _ string) bool {
		c.send(reply[:n]...)
		c.close()
		return true
	}
}

// testGuard bounds every wait of a test that runs in real time.
const testGuard = 10 * time.Second

// testContext returns a context that ends after testGuard.
func testContext(t *testing.T) context.Context {
	t.Helper()
	ctx, cancel := context.WithTimeout(t.Context(), testGuard)
	t.Cleanup(cancel)
	return ctx
}

// statementsOf returns the statements among the commands the fake recorded, without ping and quit.
func statementsOf(commands []string) []string {
	var out []string
	for _, c := range commands {
		if c != "ping" && c != "quit" && c != "command" {
			out = append(out, c)
		}
	}
	return out
}
