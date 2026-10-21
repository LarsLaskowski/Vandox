package mariadb

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"net"
	"os"
	"slices"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"testing/synctest"
	"time"
)

// openFake opens a connection to a fake server and closes it when the test ends.
func openFake(t *testing.T, srv *fakeServer) *conn {
	t.Helper()
	c, err := open(testContext(t), srv.dial, time.Now().Add(testGuard))
	if err != nil {
		t.Fatalf("open() = %v, want a connection", err)
	}
	t.Cleanup(c.close)
	return c
}

// wantError reports a test error unless err has exactly the text want.
func wantError(t *testing.T, what string, err error, want string) {
	t.Helper()
	if err == nil || err.Error() != want {
		t.Errorf("%s error = %v, want %q", what, err, want)
	}
}

// collected is what a statement delivered: the cells of every row and the number of cells that were cut.
type collected struct {
	rows [][]textCell
	cut  int
}

// collect runs a statement and reads every cell of every row with the columns of the statement.
func collect(ctx context.Context, c *conn, s statement) (collected, error) {
	var got collected
	err := c.query(ctx, s, func(p *packetReader) error {
		row := make([]textCell, 0, len(s.columns()))
		for _, col := range s.columns() {
			cl, err := p.readCell(s.step(), col)
			if err != nil {
				return err
			}
			if cl.cut {
				got.cut++
			}
			row = append(row, textCell{value: string(cl.value), null: cl.null})
		}
		got.rows = append(got.rows, row)
		return nil
	})
	return got, err
}

// agentHandshakePayload is the handshake response the plan fixes: the captured client's without LOCAL_FILES,
// MULTI_STATEMENTS and CONNECT_ATTRS, the user, no authentication data, the plugin, and nothing else.
func agentHandshakePayload() []byte {
	out := binary.LittleEndian.AppendUint32(nil, agentCapabilities)
	out = binary.LittleEndian.AppendUint32(out, 0x00100000)
	out = append(out, 45)
	out = append(out, make([]byte, 19)...)
	out = binary.LittleEndian.AppendUint32(out, agentExtended)
	out = append(out, "vandox-agent"...)
	out = append(out, 0, 0)
	out = append(out, authPlugin...)
	return append(out, 0)
}

func TestOpen_CapturedLogin(t *testing.T) {
	for _, tc := range []struct {
		name string
		load func(*testing.T) *capture
		id   uint32
	}{
		{"select capture", loadSelectCapture, 0x5670},
		{"account capture", loadAccountCapture, 0x5672},
	} {
		t.Run(tc.name, func(t *testing.T) {
			srv := newFakeServer(t, fakeConfig{Capture: tc.load(t)})

			c := openFake(t, srv)
			gotID := c.id()
			c.close()
			srv.wait()

			if gotID != tc.id {
				t.Errorf("connection id = %d, want %d, the id of the captured greeting", gotID, tc.id)
			}
			commands, _, handshakes := srv.snapshot()
			if !slices.Equal(commands, []string{"quit"}) {
				t.Errorf("commands = %q, want only the COM_QUIT of close", commands)
			}
			if len(handshakes) != 1 || len(srv.refused()) != 0 {
				t.Errorf("handshakes = %d, refused = %v, want one that the fake accepted", len(handshakes), srv.refused())
			}
		})
	}
}

func TestOpen_SendsOnlyTheUserAndNoCredential(t *testing.T) {
	srv := newFakeServer(t, fakeConfig{})

	c := openFake(t, srv)
	c.close()
	srv.wait()

	_, received, handshakes := srv.snapshot()
	if len(handshakes) != 1 {
		t.Fatalf("handshakes = %d, want 1", len(handshakes))
	}
	h := handshakes[0]
	if !bytes.Equal(h.payload, agentHandshakePayload()) {
		t.Errorf("handshake payload = %x, want %x", h.payload, agentHandshakePayload())
	}
	if h.seq != 1 || h.caps != agentCapabilities || h.ext != agentExtended || h.maxPacket != 0x00100000 || h.charset != 45 {
		t.Errorf("handshake seq %d caps %#x extended %#x max packet %#x charset %d, want 1, %#x, %#x, 0x100000, 45",
			h.seq, h.caps, h.ext, h.maxPacket, h.charset, agentCapabilities, agentExtended)
	}
	if h.user != User || h.authLen != 0 || len(h.auth) != 0 || h.plugin != authPlugin || len(h.rest) != 0 {
		t.Errorf("user %q auth data %x plugin %q rest %x, want %q, no auth data, %q and nothing more", h.user, h.auth, h.plugin, h.rest, User, authPlugin)
	}
	if len(received) != 2 {
		t.Errorf("client packets = %d, want the handshake response and the COM_QUIT", len(received))
	}
}

func TestOpen_AuthenticationSwitchGetsOneEmptyAnswerAndNoCredential(t *testing.T) {
	for _, tc := range []struct {
		name    string
		answers [][]byte
		want    string
	}{
		{"then OK", [][]byte{authSwitchFromDocs(), nil}, ""},
		{"then 1045", [][]byte{authSwitchFromDocs(), loginErrorFromDocs(1045, "28000", "Access denied")}, "mariadb: login: server error 1045"},
		{"then a second switch", [][]byte{authSwitchFromDocs(), authSwitchFromDocs()}, "mariadb: login: authentication switch refused"},
		{"then a continuation request", [][]byte{authSwitchFromDocs(), authMoreDataFromDocs()}, "mariadb: login: authentication switch refused"},
		{"a continuation request first", [][]byte{authMoreDataFromDocs()}, "mariadb: login: authentication switch refused"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			sel := loadSelectCapture(t)
			answers := slices.Clone(tc.answers)
			if answers[len(answers)-1] == nil {
				answers[len(answers)-1] = sel.loginOK.payload
			}
			srv := newFakeServer(t, fakeConfig{LoginAnswers: answers})

			c, err := open(testContext(t), srv.dial, time.Now().Add(testGuard))
			if c != nil {
				c.close()
			}
			srv.wait()

			if tc.want == "" && err != nil {
				t.Errorf("open() = %v, want a connection", err)
			}
			if tc.want != "" {
				wantError(t, "open()", err, tc.want)
			}
			checkSwitchAnswers(t, srv)
		})
	}
}

// checkSwitchAnswers fails unless the client sent a handshake, at most one empty answer and no credential, no
// scramble of the greeting and no password.
func checkSwitchAnswers(t *testing.T, srv *fakeServer) {
	t.Helper()
	_, received, handshakes := srv.snapshot()
	greeting, _ := parseGreeting(srv.capture.greeting.payload)
	empty := 0
	for i, p := range received {
		if len(p.payload) == 0 {
			empty++
		}
		if i > 0 && len(p.payload) > 0 && !bytes.Equal(p.payload, []byte{commandQuit}) {
			t.Errorf("client packet %d = %x, want the answer to the switch to be empty", i, p.payload)
		}
		if bytes.Contains(p.payload, greeting.scramble1) {
			t.Errorf("client packet %d holds the scramble of the greeting", i)
		}
	}
	if len(handshakes) != 1 || empty > 1 {
		t.Errorf("handshakes = %d, empty answers = %d, want 1 and at most 1", len(handshakes), empty)
	}
}

func TestOpen_AnswersASwitchWithOneEmptyPacketAtTheNextSequenceNumber(t *testing.T) {
	sel := loadSelectCapture(t)
	srv := newFakeServer(t, fakeConfig{LoginAnswers: [][]byte{authSwitchFromDocs(), sel.loginOK.payload}})

	c := openFake(t, srv)
	c.close()
	srv.wait()

	_, received, _ := srv.snapshot()
	if len(received) < 2 || received[1].seq != 3 || len(received[1].payload) != 0 {
		t.Errorf("client packets = %v, want the second to be empty with sequence number 3", received)
	}
}

func TestOpen_ServerErrorsBeforeAndAtTheLogin(t *testing.T) {
	const sentinel = "SENTINEL-MESSAGE-OF-THE-SERVER"
	for _, tc := range []struct {
		name   string
		config fakeConfig
		step   string
		code   uint16
	}{
		{"1040 instead of the greeting", fakeConfig{Greeting: greetingErrorFromDocs(1040, sentinel)}, "greeting", 1040},
		{"1053 instead of the greeting", fakeConfig{Greeting: greetingErrorFromDocs(1053, sentinel)}, "greeting", 1053},
		{"another error number instead of the greeting", fakeConfig{Greeting: greetingErrorFromDocs(65535, sentinel)}, "greeting", 65535},
		{"1698 at the login", fakeConfig{LoginAnswers: [][]byte{loginErrorFromDocs(1698, "28000", sentinel)}}, "login", 1698},
		{"1045 at the login", fakeConfig{LoginAnswers: [][]byte{loginErrorFromDocs(1045, "28000", sentinel)}}, "login", 1045},
		{"1226 at the login", fakeConfig{LoginAnswers: [][]byte{loginErrorFromDocs(1226, "42000", sentinel)}}, "login", 1226},
		{"error number 1 at the login", fakeConfig{LoginAnswers: [][]byte{loginErrorFromDocs(1, "HY000", sentinel)}}, "login", 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			srv := newFakeServer(t, tc.config)

			c, err := open(testContext(t), srv.dial, time.Now().Add(testGuard))
			if c != nil {
				c.close()
			}

			want := "mariadb: " + tc.step + ": server error " + strconv.Itoa(int(tc.code))
			wantError(t, "open()", err, want)
			var se *serverError
			if !errors.As(err, &se) || se.step != tc.step || se.code != tc.code {
				t.Errorf("error = %#v, want a *serverError with step %q and code %d", err, tc.step, tc.code)
			}
		})
	}
}

func TestOpen_RefusesGreetingsOutsideTheCapturedLayout(t *testing.T) {
	captured := loadSelectCapture(t).greeting
	g, ok := parseGreeting(captured.payload)
	if !ok {
		t.Fatal("unreadable captured greeting")
	}
	pad := func(n int) []byte {
		return append(captured.payload[:len(captured.payload):len(captured.payload)], make([]byte, n)...)
	}
	change := func(f func(*greeting)) []capturePacket {
		c := g
		f(&c)
		return []capturePacket{{seq: 0, payload: c.bytes()}}
	}
	for _, tc := range []struct {
		name     string
		greeting []capturePacket
		want     string
	}{
		{"protocol 9", change(func(g *greeting) { g.protocol = 9 }), "mariadb: greeting: unexpected reply"},
		{"MySQL (capability bit 0 set)", change(func(g *greeting) { g.caps |= 1 }), "mariadb: greeting: unexpected reply"},
		{"without PROTOCOL_41", change(func(g *greeting) { g.caps &^= capProtocol41 }), "mariadb: greeting: unexpected reply"},
		{"without SECURE_CONNECTION", change(func(g *greeting) { g.caps &^= capSecureConnection }), "mariadb: greeting: unexpected reply"},
		{"without PLUGIN_AUTH", change(func(g *greeting) { g.caps &^= capPluginAuth }), "mariadb: greeting: unexpected reply"},
		{"without SESSION_TRACK", change(func(g *greeting) { g.caps &^= capSessionTrack }), "mariadb: greeting: unexpected reply"},
		{"without extended metadata", change(func(g *greeting) { g.ext &^= extMetadata }), "mariadb: greeting: unexpected reply"},
		{"without cache metadata", change(func(g *greeting) { g.ext &^= extCacheMetadata }), "mariadb: greeting: unexpected reply"},
		{"of 1,025 bytes", []capturePacket{{seq: 0, payload: pad(1025 - len(captured.payload))}}, "mariadb: greeting: packet too large"},
		{"with sequence number 1", []capturePacket{{seq: 1, payload: captured.payload}}, "mariadb: greeting: packet out of sequence"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			srv := newFakeServer(t, fakeConfig{Greeting: tc.greeting})

			c, err := open(testContext(t), srv.dial, time.Now().Add(testGuard))
			if c != nil {
				c.close()
			}

			wantError(t, "open()", err, tc.want)
		})
	}
}

func TestOpen_AcceptsAGreetingOf1024BytesAndAnyVersionText(t *testing.T) {
	captured := loadSelectCapture(t).greeting
	g, _ := parseGreeting(captured.payload)
	g.version = "5.5.5-10.6.12-MariaDB-0ubuntu0.22.04.1"
	payload := g.bytes()
	payload = append(payload, make([]byte, 1024-len(payload))...)
	srv := newFakeServer(t, fakeConfig{Greeting: []capturePacket{{seq: 0, payload: payload}}})

	c, err := open(testContext(t), srv.dial, time.Now().Add(testGuard))
	if c != nil {
		c.close()
	}

	if err != nil {
		t.Errorf("open() with a greeting of 1,024 bytes = %v, want a connection", err)
	}
}

func TestOpen_RefusesOtherLoginAnswers(t *testing.T) {
	for _, tc := range []struct {
		name   string
		answer []byte
	}{
		{"an EOF packet", eofPayload(0)},
		{"a column count", []byte{0x01, 0x01}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			srv := newFakeServer(t, fakeConfig{LoginAnswers: [][]byte{tc.answer}})

			c, err := open(testContext(t), srv.dial, time.Now().Add(testGuard))
			if c != nil {
				c.close()
			}

			wantError(t, "open()", err, "mariadb: login: unexpected reply")
		})
	}
}

func TestOpen_DialFailuresHaveTheErrnoTextAndNoPath(t *testing.T) {
	const secret = "/secret/place/mysqld.sock"
	wrap := func(errno syscall.Errno) error {
		return &net.OpError{Op: "dial", Net: "unix", Addr: &net.UnixAddr{Name: secret, Net: "unix"}, Err: os.NewSyscallError("connect", errno)}
	}
	for _, tc := range []struct {
		name  string
		err   error
		errno syscall.Errno
		want  string
	}{
		{"missing path", wrap(syscall.ENOENT), syscall.ENOENT, "mariadb: connect: no such file or directory"},
		{"refused", wrap(syscall.ECONNREFUSED), syscall.ECONNREFUSED, "mariadb: connect: connection refused"},
		{"backlog full", wrap(syscall.EAGAIN), syscall.EAGAIN, "mariadb: connect: resource temporarily unavailable"},
		{"permission denied", wrap(syscall.EACCES), syscall.EACCES, "mariadb: connect: permission denied"},
		{"a bare errno", syscall.EACCES, syscall.EACCES, "mariadb: connect: permission denied"},
		{"no errno", errors.New("dial " + secret + ": something odd"), 0, "mariadb: connect: failed"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			dial := func(context.Context) (net.Conn, error) { return nil, tc.err }

			c, err := open(testContext(t), dial, time.Now().Add(testGuard))

			if c != nil {
				t.Errorf("open() returned a connection for a failed dial")
			}
			wantError(t, "open()", err, tc.want)
			var de *dialError
			if !errors.As(err, &de) || de.errno != tc.errno {
				t.Errorf("error = %#v, want a *dialError with errno %v", err, tc.errno)
			}
			if err != nil && strings.Contains(err.Error(), secret) {
				t.Errorf("error %q holds the socket path", err)
			}
		})
	}
}

func TestOpen_UnixSocketFailuresOnLinux(t *testing.T) {
	t.Chdir(t.TempDir())
	lis, err := net.Listen("unix", "closed.sock")
	if err != nil {
		t.Fatalf("listening: %v", err)
	}
	lis.(*net.UnixListener).SetUnlinkOnClose(false)
	if err := lis.Close(); err != nil {
		t.Fatalf("closing the listener: %v", err)
	}
	if err := os.WriteFile("regular", []byte("not a socket"), 0o600); err != nil {
		t.Fatalf("writing a regular file: %v", err)
	}
	for _, tc := range []struct {
		name, path string
		errno      syscall.Errno
	}{
		{"no file", "missing.sock", syscall.ENOENT},
		{"a socket file without a listener", "closed.sock", syscall.ECONNREFUSED},
		{"a regular file", "regular", syscall.ECONNREFUSED},
	} {
		t.Run(tc.name, func(t *testing.T) {
			c, err := open(testContext(t), UnixDialer(tc.path), time.Now().Add(testGuard))

			if c != nil {
				c.close()
			}
			var de *dialError
			if !errors.As(err, &de) || de.errno != tc.errno {
				t.Errorf("open(%s) error = %v, want a *dialError with errno %v", tc.path, err, tc.errno)
			}
			wantError(t, "open("+tc.path+")", err, "mariadb: connect: "+tc.errno.Error())
		})
	}
}

func TestUnixDialer_ConnectsToAListeningSocket(t *testing.T) {
	t.Chdir(t.TempDir())
	lis, err := net.Listen("unix", "live.sock")
	if err != nil {
		t.Fatalf("listening: %v", err)
	}
	t.Cleanup(func() { _ = lis.Close() })

	c, err := UnixDialer("live.sock")(testContext(t))

	if err != nil {
		t.Fatalf("UnixDialer(live.sock)() = %v, want a connection", err)
	}
	defer func() { _ = c.Close() }()
	if network := c.RemoteAddr().Network(); network != "unix" {
		t.Errorf("network = %q, want unix", network)
	}
}

func TestPing_AnswersAndRefusals(t *testing.T) {
	for _, tc := range []struct {
		name string
		ping []byte
		want string
	}{
		{"OK", nil, ""},
		{"error 1053", statementErrorFromDocs(1053, "08S01", "Server shutdown in progress"), "mariadb: ping: server error 1053"},
		{"an authentication request", authMoreDataFromDocs(), "mariadb: ping: unexpected reply"},
		{"a column count", []byte{0x01, 0x01}, "mariadb: ping: unexpected reply"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			srv := newFakeServer(t, fakeConfig{Ping: tc.ping})
			c := openFake(t, srv)

			err := c.ping(testContext(t))

			if tc.want == "" && err != nil {
				t.Errorf("ping() = %v, want nil", err)
			}
			if tc.want != "" {
				wantError(t, "ping()", err, tc.want)
			}
			c.close()
			srv.wait()
			if commands, _, _ := srv.snapshot(); !slices.Equal(commands, []string{"ping", "quit"}) {
				t.Errorf("commands = %q, want ping and quit", commands)
			}
		})
	}
}

func TestQuery_CapturedResultSets(t *testing.T) {
	sel, account := loadSelectCapture(t), loadAccountCapture(t)
	t.Run("select capture", func(t *testing.T) {
		checkCapturedStatements(t, sel, []statement{globalStatus, globalVariables, fullProcesslist},
			[]int{571, 696, 2}, []string{statusText, variablesText, processlistText})
	})
	t.Run("account capture", func(t *testing.T) {
		checkCapturedStatements(t, account, []statement{currentUser, userPrivileges},
			[]int{1, 1}, []string{currentUserText, privilegesText})
	})
}

// checkCapturedStatements runs the statements of a capture on one connection and compares every row with the capture.
func checkCapturedStatements(t *testing.T, c *capture, statements []statement, counts []int, texts []string) {
	t.Helper()
	srv := newFakeServer(t, fakeConfig{Capture: c})
	conn := openFake(t, srv)
	for i, s := range statements {
		got, err := collect(testContext(t), conn, s)
		if err != nil {
			t.Fatalf("query(%s) = %v, want the captured rows", s.step(), err)
		}
		want := decodeResultSet(t, c.command(t, texts[i]).reply)
		if len(got.rows) != counts[i] || !equalRows(got.rows, want.rows) || got.cut != 0 {
			t.Errorf("query(%s) rows = %d, cut cells = %d, want %d rows equal to the captured ones", s.step(), len(got.rows), got.cut, counts[i])
		}
	}
	conn.close()
	srv.wait()
	if commands, _, _ := srv.snapshot(); !slices.Equal(statementsOf(commands), texts) {
		t.Errorf("statements = %q, want %q", statementsOf(commands), texts)
	}
}

// equalRows compares the rows a statement delivered with the decoded rows.
func equalRows(got, want [][]textCell) bool {
	return slices.EqualFunc(got, want, slices.Equal[[]textCell])
}

func TestQuery_CapturedCellsHoldTheFactsOfThePlan(t *testing.T) {
	sel, account := loadSelectCapture(t), loadAccountCapture(t)
	srv := newFakeServer(t, fakeConfig{Capture: sel})
	c := openFake(t, srv)
	ctx := testContext(t)
	status, err := collect(ctx, c, globalStatus)
	if err != nil {
		t.Fatalf("query(status) = %v", err)
	}
	vars, err := collect(ctx, c, globalVariables)
	if err != nil {
		t.Fatalf("query(variables) = %v", err)
	}
	threads, err := collect(ctx, c, fullProcesslist)
	if err != nil {
		t.Fatalf("query(processlist) = %v", err)
	}

	checkStatusFacts(t, status.rows)
	checkVariableFacts(t, vars.rows)
	checkProcesslistFacts(t, threads.rows, c.id())
	checkAccountFacts(t, account)
}

// lookup returns the value of a name in a two-column result.
func lookup(rows [][]textCell, name string) (string, bool) {
	for _, r := range rows {
		if r[0].value == name {
			return r[1].value, true
		}
	}
	return "", false
}

// checkStatusFacts checks fact 7 for SHOW GLOBAL STATUS.
func checkStatusFacts(t *testing.T, rows [][]textCell) {
	t.Helper()
	if v, ok := lookup(rows, "Memory_used"); !ok || v != "483689568" {
		t.Errorf("Memory_used = %q (%v), want 483689568", v, ok)
	}
	if v, ok := lookup(rows, "Threads_running"); !ok || v != "1" {
		t.Errorf("Threads_running = %q (%v), want 1", v, ok)
	}
	nonNumeric, names := 0, map[string]bool{}
	for _, r := range rows {
		if _, err := strconv.ParseUint(r[1].value, 10, 64); err != nil {
			nonNumeric++
		}
		names[strings.ToLower(r[0].value)] = true
	}
	if nonNumeric != 41 || len(names) != len(rows) {
		t.Errorf("non-numeric status values = %d, distinct names ignoring case = %d of %d rows, want 41 and all distinct", nonNumeric, len(names), len(rows))
	}
}

// checkVariableFacts checks fact 7 for SHOW GLOBAL VARIABLES.
func checkVariableFacts(t *testing.T, rows [][]textCell) {
	t.Helper()
	for name, want := range map[string]string{
		"extra_port": "0", "socket": "/run/mysqld/mysqld.sock", "bind_address": "127.0.0.1", "local_infile": "OFF", "max_allowed_packet": "16777216",
	} {
		if v, ok := lookup(rows, name); !ok || v != want {
			t.Errorf("variable %s = %q (%v), want %q", name, v, ok, want)
		}
	}
	longest, negative, names := 0, 0, map[string]bool{}
	for _, r := range rows {
		longest = max(longest, len(r[1].value))
		if strings.HasPrefix(r[0].value, "performance_schema_") && r[1].value == "-1" {
			negative++
		}
		names[strings.ToLower(r[0].value)] = true
	}
	if longest != 879 || negative == 0 || len(names) != len(rows) {
		t.Errorf("longest value = %d bytes, performance_schema_* values of -1 = %d, distinct names = %d of %d, want 879, some and all distinct", longest, negative, len(names), len(rows))
	}
}

// checkProcesslistFacts checks fact 8 for SHOW FULL PROCESSLIST: the sleeping Plesk connection has NULL in Info and
// the agent's own row, which has the greeting's connection id, has NULL in db.
func checkProcesslistFacts(t *testing.T, rows [][]textCell, ownID uint32) {
	t.Helper()
	own, sleeping := false, false
	for _, r := range rows {
		if len(r) != 9 {
			t.Fatalf("process list row has %d cells, want 9", len(r))
		}
		own = own || (r[0].value == strconv.FormatUint(uint64(ownID), 10) && r[3].null && r[1].value == User)
		sleeping = sleeping || (r[4].value == "Sleep" && r[7].null)
	}
	if len(rows) != 2 || !own || !sleeping {
		t.Errorf("process list rows = %d, own row with NULL db = %v, sleeping row with NULL Info = %v, want 2, true, true", len(rows), own, sleeping)
	}
}

// checkAccountFacts checks fact 9 against the account capture.
func checkAccountFacts(t *testing.T, account *capture) {
	t.Helper()
	srv := newFakeServer(t, fakeConfig{Capture: account})
	c := openFake(t, srv)
	user, err := collect(testContext(t), c, currentUser)
	if err != nil {
		t.Fatalf("query(account) = %v", err)
	}
	privileges, err := collect(testContext(t), c, userPrivileges)
	if err != nil {
		t.Fatalf("query(account) = %v", err)
	}
	if !equalRows(user.rows, [][]textCell{{text("vandox-agent@localhost")}}) || !equalRows(privileges.rows, [][]textCell{{text("PROCESS")}}) {
		t.Errorf("account rows = %v and %v, want vandox-agent@localhost and PROCESS", user.rows, privileges.rows)
	}
}

func TestStatement_TextsStepsAndColumns(t *testing.T) {
	cut := func(name string, limit int) column { return column{name: name, limit: limit, cut: true} }
	strict := func(name string, limit int) column { return column{name: name, limit: limit} }
	for _, tc := range []struct {
		s       statement
		text    string
		step    string
		columns []column
	}{
		{globalStatus, statusText, "status", []column{cut("Variable_name", 64), cut("Value", 1025)}},
		{globalVariables, variablesText, "variables", []column{cut("Variable_name", 64), cut("Value", 1025)}},
		{fullProcesslist, processlistText, "processlist", []column{
			strict("Id", 20), cut("User", 512), cut("Host", 512), cut("db", 256), cut("Command", 32), strict("Time", 20),
			cut("State", 256), cut("Info", 16384), strict("Progress", 64),
		}},
		{currentUser, currentUserText, "account", []column{cut("CURRENT_USER()", 1024)}},
		{userPrivileges, privilegesText, "account", []column{cut("PRIVILEGE_TYPE", 64)}},
	} {
		t.Run(tc.step+" "+tc.text[:min(len(tc.text), 24)], func(t *testing.T) {
			if got := tc.s.text(); got != tc.text {
				t.Errorf("text() = %q, want %q", got, tc.text)
			}
			if got := tc.s.step(); got != tc.step {
				t.Errorf("step() = %q, want %q", got, tc.step)
			}
			if got := tc.s.columns(); !slices.Equal(got, tc.columns) {
				t.Errorf("columns() = %v, want %v", got, tc.columns)
			}
		})
	}
}

func TestStatement_TextsAreTheCapturedOnes(t *testing.T) {
	sel, account := loadSelectCapture(t), loadAccountCapture(t)
	var captured []string
	for _, c := range append(slices.Clone(sel.commands), account.commands...) {
		captured = append(captured, c.text())
	}
	var texts []string
	for _, s := range []statement{globalStatus, globalVariables, fullProcesslist, currentUser, userPrivileges} {
		texts = append(texts, s.text())
	}
	if !slices.Equal(texts, captured) {
		t.Errorf("statement texts = %q, want the captured %q", texts, captured)
	}
}

// goodStatusReply is a status reply in the captured layout with two rows.
func goodStatusReply(defs [][]byte) []capturePacket {
	return resultSetPackets(defs, [][]textCell{{text("Aborted_clients"), text("4")}, {text("Threads_running"), text("1")}})
}

// withSeq returns a copy of the reply with the sequence number of packet i changed.
func withSeq(reply []capturePacket, i int, seq byte) []capturePacket {
	out := slices.Clone(reply)
	out[i].seq = seq
	return out
}

// withPayload returns a copy of the reply with the payload of packet i replaced.
func withPayload(reply []capturePacket, i int, payload []byte) []capturePacket {
	out := slices.Clone(reply)
	out[i].payload = payload
	return out
}

// malformedCase is a reply the client must refuse and the exact error text of the refusal.
type malformedCase struct {
	name      string
	statement statement
	reply     []capturePacket
	want      string
}

func statusCases(defs [][]byte) []malformedCase {
	good := goodStatusReply(defs)
	rows := len(good) - 3 // the index of the first row is 3
	renamed := bytes.Replace(defs[0], []byte("Variable_name"), []byte("Variable_nxme"), 1)
	last := len(good) - 1
	return []malformedCase{
		{"reply starts at sequence number 2", globalStatus, withSeq(good, 0, 2), "mariadb: status: packet out of sequence"},
		{"a row out of sequence", globalStatus, withSeq(good, rows, 40), "mariadb: status: packet out of sequence"},
		{"a column definition above 64 KiB", globalStatus, withPayload(good, 1, make([]byte, 64*1024+1)), "mariadb: status: packet too large"},
		{"an OK packet instead of a result set", globalStatus, sequenced(pingOKFromDocs()), "mariadb: status: unexpected reply"},
		{"no metadata follows", globalStatus, withPayload(good, 0, []byte{2, 0}), "mariadb: status: unexpected reply"},
		{"three columns", globalStatus, withPayload(good, 0, []byte{3, 1}), "mariadb: status: unexpected columns"},
		{"one column", globalStatus, withPayload(good, 0, []byte{1, 1}), "mariadb: status: unexpected columns"},
		{"another column name", globalStatus, withPayload(good, 1, renamed), "mariadb: status: unexpected columns"},
		{"a local file request", globalStatus, sequenced(append([]byte{0xfb}, "/etc/passwd"...)), "mariadb: status: local file request refused"},
		{"more results after the definitions", globalStatus, withPayload(good, 3, eofPayload(statusCaptured|statusMoreResults)), "mariadb: status: more results refused"},
		{"more results after the rows", globalStatus, withPayload(good, last, eofPayload(statusCaptured|statusMoreResults)), "mariadb: status: more results refused"},
		{"an error instead of the result", globalStatus, sequenced(statementErrorFromDocs(1227, "42000", "Access denied; you need PROCESS")), "mariadb: status: server error 1227"},
		{"an error after the rows", globalStatus, withPayload(good, last, statementErrorFromDocs(1317, "70100", "Query execution was interrupted")), "mariadb: status: server error 1317"},
		{"an error in place of a column definition", globalStatus, sequenced([]byte{2, 1}, statementErrorFromDocs(1146, "42S02", "Table doesn't exist")), "mariadb: status: server error 1146"},
		{"a cell longer than its row", globalStatus, withPayload(good, rows, append([]byte{200}, "abc"...)), "mariadb: status: malformed packet"},
		{"a cell length of 0xff", globalStatus, withPayload(good, rows, append(rowPayload(text("Aborted_clients")), 0xff, 'x')), "mariadb: status: malformed packet"},
		{"a truncated length-encoded integer", globalStatus, withPayload(good, rows, append(rowPayload(text("Aborted_clients")), 0xfc, 0x01)), "mariadb: status: malformed packet"},
		{"a row without its last cell", globalStatus, withPayload(good, rows, rowPayload(text("Aborted_clients"))), "mariadb: status: malformed packet"},
	}
}

func processlistCases(defs [][]byte) []malformedCase {
	row := func(id, seconds, progress string) []textCell {
		return processlistRow(id, "root", "localhost", "db", "Query", seconds, "executing", "SELECT 1", progress)
	}
	reply := func(r []textCell) []capturePacket { return resultSetPackets(defs, [][]textCell{r}) }
	return []malformedCase{
		{"an Id of 21 bytes", fullProcesslist, reply(row("123456789012345678901", "1", "0.000")), "mariadb: processlist: cell too long"},
		{"a Time of 21 bytes", fullProcesslist, reply(row("1", "123456789012345678901", "0.000")), "mariadb: processlist: cell too long"},
		{"a Progress of 65 bytes", fullProcesslist, reply(row("1", "1", strings.Repeat("9", 65))), "mariadb: processlist: cell too long"},
		{"a process list of 8 columns", fullProcesslist, sequenced([]byte{8, 1}), "mariadb: processlist: unexpected columns"},
	}
}

func TestQuery_RefusesMalformedReplies(t *testing.T) {
	sel := loadSelectCapture(t)
	cases := slices.Concat(statusCases(capturedDefinitions(t, sel, statusText)), processlistCases(capturedDefinitions(t, sel, processlistText)))
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			srv := newFakeServer(t, fakeConfig{Replies: map[string][]capturePacket{tc.statement.text(): tc.reply}})
			c := openFake(t, srv)

			_, err := collect(testContext(t), c, tc.statement)

			wantError(t, "query("+tc.statement.step()+")", err, tc.want)
		})
	}
}

func TestQuery_RefusalsHaveTheirTypes(t *testing.T) {
	sel := loadSelectCapture(t)
	defs := capturedDefinitions(t, sel, statusText)
	srv := newFakeServer(t, fakeConfig{Replies: map[string][]capturePacket{statusText: withPayload(goodStatusReply(defs), 0, []byte{3, 1})}})
	c := openFake(t, srv)

	_, err := collect(testContext(t), c, globalStatus)

	var pe *protocolError
	if !errors.As(err, &pe) || pe.step != "status" || pe.reason != "unexpected columns" {
		t.Errorf("error = %#v, want a *protocolError with step status and reason unexpected columns", err)
	}
}

func TestQuery_LocalFileRequestIsNotAnswered(t *testing.T) {
	srv := newFakeServer(t, fakeConfig{Replies: map[string][]capturePacket{statusText: sequenced(append([]byte{0xfb}, "/etc/passwd"...))}})
	c := openFake(t, srv)

	_, err := collect(testContext(t), c, globalStatus)
	c.close()
	srv.wait()

	wantError(t, "query(status)", err, "mariadb: status: local file request refused")
	_, received, _ := srv.snapshot()
	if len(received) < 2 || len(received) > 3 || (len(received) == 3 && !bytes.Equal(received[2].payload, []byte{commandQuit})) {
		t.Errorf("client packets = %v, want the handshake, the statement and at most a COM_QUIT, nothing like the file", received)
	}
}

func TestQuery_ErrorTextsNeverHoldBytesTheServerSent(t *testing.T) {
	const sentinel = "SENTINEL-FROM-THE-SERVER"
	replies := map[string][]capturePacket{
		statusText:      sequenced(statementErrorFromDocs(1227, "42000", sentinel)),
		variablesText:   sequenced(append([]byte{0xfb}, sentinel...)),
		processlistText: sequenced([]byte{9, 1}, []byte(sentinel)),
	}
	srv := newFakeServer(t, fakeConfig{Replies: replies})

	for _, s := range []statement{globalStatus, globalVariables, fullProcesslist} {
		c := openFake(t, srv)
		_, err := collect(testContext(t), c, s)
		if err == nil || strings.Contains(err.Error(), sentinel) {
			t.Errorf("query(%s) error = %v, want an error without the bytes the server sent", s.step(), err)
		}
	}
}

func TestQuery_ClosedConnectionIsReported(t *testing.T) {
	sel := loadSelectCapture(t)
	good := goodStatusReply(capturedDefinitions(t, sel, statusText))
	for _, n := range []int{0, 1, 3, 4, len(good) - 1} {
		t.Run("after "+strconv.Itoa(n)+" packets", func(t *testing.T) {
			srv := newFakeServer(t, fakeConfig{OnCommand: closeAfter(good, n)})
			c := openFake(t, srv)

			_, err := collect(testContext(t), c, globalStatus)

			wantError(t, "query(status)", err, "mariadb: status: connection closed")
			if !errors.Is(err, errClosed) {
				t.Errorf("error %v is not errClosed", err)
			}
		})
	}
}

func TestQuery_StopsAtTheErrorOfTheRowFunction(t *testing.T) {
	errStop := errors.New("stop here")
	srv := newFakeServer(t, fakeConfig{})
	c := openFake(t, srv)
	rows := 0

	err := c.query(testContext(t), globalStatus, func(*packetReader) error {
		rows++
		return errStop
	})

	if !errors.Is(err, errStop) || rows != 1 {
		t.Errorf("query() = %v after %d rows, want the error of the row function after the first row", err, rows)
	}
}

func TestOpen_NoGreetingIsNoAnswerInTime(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		srv := newFakeServer(t, fakeConfig{NoGreeting: true})
		start := time.Now()
		ctx, cancel := context.WithDeadline(t.Context(), start.Add(4*time.Second))
		defer cancel()

		c, err := open(ctx, srv.dial, start.Add(2*time.Second))

		if c != nil {
			c.close()
		}
		wantError(t, "open()", err, "mariadb: greeting: no answer in time")
		if !errors.Is(err, errTimeout) {
			t.Errorf("error %v is not errTimeout", err)
		}
		if elapsed := time.Since(start); elapsed != 2*time.Second {
			t.Errorf("open() returned after %v, want the greeting budget of 2s", elapsed)
		}
	})
}

func TestOpen_SilentLoginEndsAtTheContextDeadline(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		srv := newFakeServer(t, fakeConfig{SilentLogin: true})
		start := time.Now()
		ctx, cancel := context.WithDeadline(t.Context(), start.Add(4*time.Second))
		defer cancel()

		c, err := open(ctx, srv.dial, start.Add(2*time.Second))

		if c != nil {
			c.close()
		}
		wantError(t, "open()", err, "mariadb: login: no answer in time")
		if elapsed := time.Since(start); elapsed != 4*time.Second {
			t.Errorf("open() returned after %v, want the deadline of the run at 4s", elapsed)
		}
	})
}

func TestPing_NoAnswerEndsAtTheContextDeadline(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		srv := newFakeServer(t, fakeConfig{OnCommand: hangOn("ping")})
		c := openFake(t, srv)
		start := time.Now()
		ctx, cancel := context.WithDeadline(t.Context(), start.Add(4*time.Second))
		defer cancel()

		err := c.ping(ctx)

		wantError(t, "ping()", err, "mariadb: ping: no answer in time")
		if elapsed := time.Since(start); elapsed != 4*time.Second || !errors.Is(err, errTimeout) {
			t.Errorf("ping() returned after %v with %v, want 4s and errTimeout", elapsed, err)
		}
	})
}

func TestQuery_NoAnswerEndsAtTheContextDeadline(t *testing.T) {
	for _, tc := range []struct {
		name string
		s    statement
		step string
	}{
		{"status", globalStatus, "status"},
		{"variables", globalVariables, "variables"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				srv := newFakeServer(t, fakeConfig{OnCommand: hangOn(tc.s.text())})
				c := openFake(t, srv)
				start := time.Now()
				ctx, cancel := context.WithDeadline(t.Context(), start.Add(4*time.Second))
				defer cancel()

				_, err := collect(ctx, c, tc.s)

				wantError(t, "query("+tc.step+")", err, "mariadb: "+tc.step+": no answer in time")
				if elapsed := time.Since(start); elapsed != 4*time.Second || !errors.Is(err, errTimeout) {
					t.Errorf("query() returned after %v with %v, want 4s and errTimeout", elapsed, err)
				}
			})
		})
	}
}

func TestQuery_CancellationEndsAWaitAtOnce(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		srv := newFakeServer(t, fakeConfig{OnCommand: hangOn(statusText)})
		c := openFake(t, srv)
		start := time.Now()
		ctx, cancel := context.WithCancel(t.Context())
		defer cancel()
		time.AfterFunc(time.Second, cancel)

		_, err := collect(ctx, c, globalStatus)

		if err == nil || time.Since(start) != time.Second {
			t.Errorf("query() = %v after %v, want an error after the cancellation at 1s", err, time.Since(start))
		}
	})
}

func TestQuery_SeveralStatementsRunOnOneConnection(t *testing.T) {
	srv := newFakeServer(t, fakeConfig{})
	c := openFake(t, srv)
	ctx := testContext(t)

	for _, s := range []statement{globalStatus, globalVariables, globalStatus} {
		if _, err := collect(ctx, c, s); err != nil {
			t.Fatalf("query(%s) = %v, want the captured rows", s.step(), err)
		}
	}
	if err := c.ping(ctx); err != nil {
		t.Errorf("ping() after the statements = %v, want nil", err)
	}

	if dials, maxOpen := srv.counts(); dials != 1 || maxOpen != 1 {
		t.Errorf("dials = %d, connections at once = %d, want 1 and 1", dials, maxOpen)
	}
}

func TestUserSQL_CreatesOnlyTheProcessUser(t *testing.T) {
	data, err := os.ReadFile("../../deploy/agent/mariadb-user.sql")
	if err != nil {
		t.Fatalf("reading deploy/agent/mariadb-user.sql: %v", err)
	}
	var comments, code []string
	for _, line := range strings.Split(string(data), "\n") {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "--") || strings.HasPrefix(trimmed, "#") {
			comments = append(comments, trimmed)
			continue
		}
		code = append(code, trimmed)
	}
	var statements []string
	for _, part := range strings.Split(strings.Join(code, " "), ";") {
		if fields := strings.Fields(part); len(fields) > 0 {
			statements = append(statements, strings.Join(fields, " "))
		}
	}

	want := []string{
		"CREATE OR REPLACE USER 'vandox-agent'@'localhost' IDENTIFIED VIA unix_socket WITH MAX_USER_CONNECTIONS 2",
		"GRANT PROCESS ON *.* TO 'vandox-agent'@'localhost'",
	}
	if !slices.Equal(statements, want) {
		t.Errorf("statements = %q, want exactly %q", statements, want)
	}
	text := strings.Join(comments, "\n")
	for _, command := range []string{"mariadb < mariadb-user.sql", "plesk db < mariadb-user.sql"} {
		if !strings.Contains(text, command) {
			t.Errorf("comments do not name %q, want both ways to run the file", command)
		}
	}
}

func TestCapture_ReplaysByteForByte(t *testing.T) {
	for _, path := range []string{selectCapturePath, accountCapturePath} {
		c := readCapture(t, path)
		replayThroughTheFake(t, c)
		checkReEncoding(t, c)
		g, ok := parseGreeting(c.greeting.payload)
		if !ok || !bytes.Equal(g.bytes(), c.greeting.payload) {
			t.Errorf("%s: the greeting does not survive being taken apart and put together", path)
		}
		h, ok := parseHandshake(c.loginRequest)
		if !ok || h.user != User || h.caps != capturedCapabilities || h.ext != capturedExtended {
			t.Errorf("%s: handshake = %+v, want the captured client %q with %#x and %#x", path, h, User, capturedCapabilities, capturedExtended)
		}
	}
}

// checkReEncoding fails unless the packets of every turn encode to the bytes of the file, by writePacket and by the helper.
func checkReEncoding(t *testing.T, c *capture) {
	t.Helper()
	for i, turn := range c.turns {
		var viaClient, viaHelper bytes.Buffer
		for _, p := range turn.packets {
			viaHelper.Write(p.bytes())
			if err := writePacket(&viaClient, p.seq, p.payload); err != nil {
				t.Errorf("%s: writePacket in turn %d = %v", c.path, i, err)
				break
			}
		}
		if !bytes.Equal(viaClient.Bytes(), turn.data) || !bytes.Equal(viaHelper.Bytes(), turn.data) {
			t.Errorf("%s: turn %d re-encodes to %d bytes by writePacket and %d by the helper, want the %d bytes of the file", c.path, i, viaClient.Len(), viaHelper.Len(), len(turn.data))
		}
	}
}

// replayThroughTheFake plays the client of a capture against the fake with raw bytes and compares every answer.
func replayThroughTheFake(t *testing.T, c *capture) {
	t.Helper()
	srv := newFakeServer(t, fakeConfig{Capture: c})
	raw, err := srv.dial(testContext(t))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	steps := []capturePacket{c.loginRequest}
	for _, cmd := range c.commands {
		steps = append(steps, cmd.request)
	}
	var expect [][]byte
	for j := 0; j < len(c.turns); j += 2 {
		expect = append(expect, c.turns[j].data)
	}
	readExactly(t, raw, expect[0], "greeting")
	for i, step := range steps {
		if _, err := raw.Write(step.bytes()); err != nil {
			t.Fatalf("writing step %d: %v", i, err)
		}
		readExactly(t, raw, expect[i+1], "answer "+strconv.Itoa(i))
	}
}

// readExactly reads len(want) bytes from the fake and compares them.
func readExactly(t *testing.T, raw net.Conn, want []byte, what string) {
	t.Helper()
	got := make([]byte, len(want))
	_ = raw.SetReadDeadline(time.Now().Add(testGuard))
	n, err := readFull(raw, got)
	if err != nil || !bytes.Equal(got[:n], want) {
		t.Fatalf("the fake sent %d of %d bytes of the %s (err %v), want exactly the captured bytes", n, len(want), what, err)
	}
}

// readFull reads until buf is full.
func readFull(r net.Conn, buf []byte) (int, error) {
	total := 0
	for total < len(buf) {
		n, err := r.Read(buf[total:])
		total += n
		if err != nil {
			return total, err
		}
	}
	return total, nil
}

// layoutCase is a handshake the fake judges and whether it must refuse it.
type layoutCase struct {
	name      string
	handshake capturePacket
	refused   bool
}

// layoutCases returns the captured handshake and variants of it that lack one layout bit or have one too many.
func layoutCases(captured capturePacket) []layoutCase {
	change := func(caps, ext uint32) capturePacket {
		p := slices.Clone(captured.payload)
		binary.LittleEndian.PutUint32(p, binary.LittleEndian.Uint32(p)&^caps)
		binary.LittleEndian.PutUint32(p[28:], binary.LittleEndian.Uint32(p[28:])&^ext)
		return capturePacket{seq: captured.seq, payload: p}
	}
	withEOF := slices.Clone(captured.payload)
	binary.LittleEndian.PutUint32(withEOF, binary.LittleEndian.Uint32(withEOF)|capDeprecateEOF)
	return []layoutCase{
		{"the captured handshake", captured, false},
		{"without PROTOCOL_41", change(capProtocol41, 0), true},
		{"without SESSION_TRACK", change(capSessionTrack, 0), true},
		{"without PLUGIN_AUTH_LENENC_CLIENT_DATA", change(capLenencAuthData, 0), true},
		{"without extended metadata", change(0, extMetadata), true},
		{"without cache metadata", change(0, extCacheMetadata), true},
		{"with DEPRECATE_EOF", capturePacket{seq: captured.seq, payload: withEOF}, true},
	}
}

func TestCapture_FakeRequiresTheCapturedLayoutBits(t *testing.T) {
	for _, tc := range layoutCases(loadSelectCapture(t).loginRequest) {
		t.Run(tc.name, func(t *testing.T) { judgeHandshake(t, tc) })
	}
	t.Run("the agent's own handshake", func(t *testing.T) {
		srv := newFakeServer(t, fakeConfig{})

		c := openFake(t, srv)
		c.close()
		srv.wait()

		if refusals := srv.refused(); len(refusals) != 0 {
			t.Errorf("the fake refused the agent's handshake: %v", refusals)
		}
	})
}

// judgeHandshake sends a handshake to a fresh fake over a raw connection and checks that it is accepted or refused.
func judgeHandshake(t *testing.T, tc layoutCase) {
	t.Helper()
	srv := newFakeServer(t, fakeConfig{})
	raw, err := srv.dial(testContext(t))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	readExactly(t, raw, srv.capture.turns[0].data, "greeting")

	answer := rawLogin(t, raw, tc.handshake)

	refusals := srv.refused()
	refusedWith1043 := len(answer.payload) >= 3 && answer.payload[0] == 0xff && binary.LittleEndian.Uint16(answer.payload[1:]) == serverBadHandshake
	if tc.refused && (len(refusals) != 1 || !refusedWith1043) {
		t.Errorf("answer = %x, refusals = %v, want error %d and one refusal", answer.payload, refusals, serverBadHandshake)
	}
	if !tc.refused && (len(refusals) != 0 || !bytes.Equal(answer.bytes(), srv.capture.loginOK.bytes())) {
		t.Errorf("answer = %x, refusals = %v, want the captured login OK and no refusal", answer.bytes(), refusals)
	}
}

// rawLogin sends a handshake response over a raw connection and reads the first packet of the answer.
func rawLogin(t *testing.T, raw net.Conn, hs capturePacket) capturePacket {
	t.Helper()
	if _, err := raw.Write(hs.bytes()); err != nil {
		t.Fatalf("writing the handshake: %v", err)
	}
	_ = raw.SetReadDeadline(time.Now().Add(testGuard))
	header := make([]byte, 4)
	if _, err := readFull(raw, header); err != nil {
		t.Fatalf("reading the answer: %v", err)
	}
	payload := make([]byte, int(header[0])|int(header[1])<<8|int(header[2])<<16)
	if _, err := readFull(raw, payload); err != nil {
		t.Fatalf("reading the answer: %v", err)
	}
	return capturePacket{seq: header[3], payload: payload}
}

func TestErrorOf_ErrorPacketsBelowThreeBytesAreMalformed(t *testing.T) {
	for _, tc := range []struct {
		name    string
		payload []byte
		want    string
	}{
		{"only the marker", []byte{0xff}, "mariadb: query: malformed packet"},
		{"the marker and one byte of the code", []byte{0xff, 0x15}, "mariadb: query: malformed packet"},
		{"the marker and the code", []byte{0xff, 0x15, 0x04}, "mariadb: query: server error 1045"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			err := errorOf("query", tc.payload)

			wantError(t, "errorOf()", err, tc.want)
		})
	}
}

func TestEndOf_ChecksTheEOFPacket(t *testing.T) {
	for _, tc := range []struct {
		name    string
		payload []byte
		want    string
	}{
		{"an EOF packet", []byte{0xfe, 0, 0, 0x02, 0}, ""},
		{"an error packet", []byte{0xff, 0x15, 0x04, '#'}, "mariadb: query: server error 1045"},
		{"an error packet without a code", []byte{0xff, 0x15}, "mariadb: query: malformed packet"},
		{"an empty packet", nil, "mariadb: query: unexpected reply"},
		{"an EOF packet cut short", []byte{0xfe, 0, 0, 0x02}, "mariadb: query: unexpected reply"},
		{"a packet of another kind", []byte{0x00, 0, 0, 0x02, 0}, "mariadb: query: unexpected reply"},
		{"an EOF packet that is long enough to be a row", []byte{0xfe, 0, 0, 0, 0, 0, 0, 0, 0}, "mariadb: query: unexpected reply"},
		{"an EOF packet with more results", []byte{0xfe, 0, 0, 0x08, 0}, "mariadb: query: more results refused"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			err := endOf("query", tc.payload)

			if tc.want == "" {
				if err != nil {
					t.Errorf("endOf() = %v, want nil", err)
				}
				return
			}
			wantError(t, "endOf()", err, tc.want)
		})
	}
}

func TestTakeString_ReadsLengthPrefixedStrings(t *testing.T) {
	for _, tc := range []struct {
		name     string
		in       []byte
		wantOK   bool
		wantStr  string
		wantRest string
	}{
		{"a string and a rest", []byte{3, 'a', 'b', 'c', 'x'}, true, "abc", "x"},
		{"an empty string", []byte{0, 'x'}, true, "", "x"},
		{"no bytes", nil, false, "", ""},
		{"a NULL marker", []byte{0xfb}, false, "", ""},
		{"a length beyond the bytes", []byte{4, 'a', 'b'}, false, "", ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s, rest, ok := takeString(tc.in)

			if ok != tc.wantOK || string(s) != tc.wantStr || string(rest) != tc.wantRest {
				t.Errorf("takeString(%v) = %q, %q, %v, want %q, %q, %v", tc.in, s, rest, ok, tc.wantStr, tc.wantRest, tc.wantOK)
			}
		})
	}
}
