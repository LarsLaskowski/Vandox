package sdbus

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"testing"
	"testing/synctest"
	"time"
)

// capturedPatterns are the patterns of the captured ListUnitsByPatterns call; the capture holds the reply to exactly them.
var capturedPatterns = []string{"plesk-php*-fpm.service", "php*-fpm.service"}

// wantPlesk is the captured ListUnitsByPatterns reply, decoded.
var wantPlesk = UnitInfo{
	ID:          "plesk-php85-fpm.service",
	LoadState:   "loaded",
	ActiveState: "active",
	SubState:    "running",
	Path:        pleskPath,
}

// testContext returns a context that ends with the test; its generous deadline only keeps a client that blocks from
// holding up the run.
func testContext(t *testing.T) context.Context {
	t.Helper()
	ctx, cancel := context.WithTimeout(t.Context(), testGuard)
	t.Cleanup(cancel)
	return ctx
}

// openSession opens a session over a pipe to a fake bus.
func openSession(t *testing.T, cfg fakeBusConfig) (*Session, *fakeBus, context.Context) {
	t.Helper()
	ctx := testContext(t)
	bus := newFakeBus(t, cfg)
	s, err := Open(ctx, bus.pipe(), testUID)
	if err != nil {
		t.Fatalf("Open(ctx, pipe, %d) = %v, want a session", testUID, err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s, bus, ctx
}

// wantError reports a test error unless err has exactly the text want.
func wantError(t *testing.T, what string, err error, want string) {
	t.Helper()
	if err == nil || err.Error() != want {
		t.Errorf("%s: error = %v, want %q", what, err, want)
	}
}

// wantErrorPrefix reports a test error unless err starts with prefix.
func wantErrorPrefix(t *testing.T, what string, err error, prefix string) {
	t.Helper()
	if err == nil || !strings.HasPrefix(err.Error(), prefix) {
		t.Errorf("%s: error = %v, want one starting with %q", what, err, prefix)
	}
}

// patternsCall lists the captured patterns on s.
func patternsCall(ctx context.Context, s *Session) error {
	_, err := s.ListUnitsByPatterns(ctx, capturedPatterns)
	return err
}

// namesCall lists three names on s.
func namesCall(ctx context.Context, s *Session) error {
	_, err := s.ListUnitsByNames(ctx, []string{"mariadb.service", "nginx.service", "cloud-config.service"})
	return err
}

// restartsCall reads NRestarts of the captured mariadb unit on s.
func restartsCall(ctx context.Context, s *Session) error {
	_, err := s.GetUint32(ctx, mariadbPath, serviceIface, "NRestarts")
	return err
}

func TestSession_ReadsCapturedUnits(t *testing.T) {
	pieces := []struct {
		name  string
		piece int
	}{{"replies written at once", 0}, {"replies written byte by byte", 1}}
	for _, p := range pieces {
		t.Run(p.name, func(t *testing.T) {
			s, _, ctx := openSession(t, fakeBusConfig{Piece: p.piece})

			got, err := s.ListUnitsByPatterns(ctx, capturedPatterns)

			if err != nil || !slices.Equal(got, []UnitInfo{wantPlesk}) {
				t.Errorf("ListUnitsByPatterns(%q) = %v, %v, want %v", capturedPatterns, got, err, []UnitInfo{wantPlesk})
			}
		})
	}
	properties := []struct {
		unit  string
		path  string
		enter uint64
	}{
		{"mariadb loaded and active", mariadbPath, 1792240371069328},
		{"not-found and inactive", notFoundPath, 0},
		{"masked and inactive", maskedPath, 0},
	}
	for _, p := range properties {
		t.Run(p.unit, func(t *testing.T) {
			s, _, ctx := openSession(t, fakeBusConfig{})

			enter, enterErr := s.GetUint64(ctx, p.path, unitIface, "ActiveEnterTimestamp")
			restarts, restartsErr := s.GetUint32(ctx, p.path, serviceIface, "NRestarts")

			if enterErr != nil || enter != p.enter {
				t.Errorf("GetUint64(%s, ActiveEnterTimestamp) = %d, %v, want %d", p.path, enter, enterErr, p.enter)
			}
			if restartsErr != nil || restarts != 0 {
				t.Errorf("GetUint32(%s, NRestarts) = %d, %v, want 0", p.path, restarts, restartsErr)
			}
		})
	}
}

func TestSession_ListUnitsByNamesInChunks(t *testing.T) {
	t.Run("captured units in order with an alias", func(t *testing.T) {
		s, bus, ctx := openSession(t, fakeBusConfig{})
		names := []string{"mariadb.service", "vandox-does-not-exist.service", "cloud-config.service", "mysql.service"}
		want := []UnitInfo{
			{ID: "mariadb.service", LoadState: "loaded", ActiveState: "active", SubState: "running", Path: mariadbPath},
			{ID: "vandox-does-not-exist.service", LoadState: "not-found", ActiveState: "inactive", SubState: "dead", Path: notFoundPath},
			{ID: "cloud-config.service", LoadState: "masked", ActiveState: "inactive", SubState: "dead", Path: maskedPath},
			{ID: "mariadb.service", LoadState: "loaded", ActiveState: "active", SubState: "running", Path: mariadbPath},
		}

		got, err := s.ListUnitsByNames(ctx, names)

		if err != nil || !slices.Equal(got, want) {
			t.Errorf("ListUnitsByNames(%q) = %v, %v, want %v", names, got, err, want)
		}
		if calls := bus.calls("ListUnitsByNames"); len(calls) != 1 {
			t.Errorf("ListUnitsByNames(%d names) made %d calls, want 1", len(names), len(calls))
		}
	})
	sizes := []struct {
		names int
		calls []int
	}{{1, []int{1}}, {64, []int{64}}, {65, []int{64, 1}}, {130, []int{64, 64, 2}}, {200, []int{64, 64, 64, 8}}}
	for _, c := range sizes {
		t.Run(fmt.Sprintf("%d names", c.names), func(t *testing.T) {
			s, bus, ctx := openSession(t, fakeBusConfig{})
			names := make([]string, c.names)
			for i := range names {
				names[i] = fmt.Sprintf("unit%03d.service", i)
			}

			got, err := s.ListUnitsByNames(ctx, names)

			if err != nil {
				t.Fatalf("ListUnitsByNames(%d names) = %v, want no error", len(names), err)
			}
			wantIDs(t, got, names)
			wantNames(t, bus, c.calls, names)
		})
	}
}

// wantIDs checks that units have the given ids in order.
func wantIDs(t *testing.T, units []UnitInfo, ids []string) {
	t.Helper()
	got := make([]string, len(units))
	for i, unit := range units {
		got[i] = unit.ID
	}
	if !slices.Equal(got, ids) {
		t.Errorf("ListUnitsByNames returned %d entries %q, want one per name in order (%d names)", len(got), got, len(ids))
	}
}

// wantNames checks that the ListUnitsByNames calls of bus carried counts names each, and all names in order.
func wantNames(t *testing.T, bus *fakeBus, counts []int, names []string) {
	t.Helper()
	var sent []string
	var gotCounts []int
	for _, call := range bus.calls("ListUnitsByNames") {
		r := &bodyReader{t: t, order: call.Order, b: call.Body}
		chunk := r.strings()
		gotCounts = append(gotCounts, len(chunk))
		sent = append(sent, chunk...)
	}
	if !slices.Equal(gotCounts, counts) {
		t.Errorf("ListUnitsByNames calls carried %v names, want %v", gotCounts, counts)
	}
	if !slices.Equal(sent, names) {
		t.Errorf("ListUnitsByNames calls carried %d names in a different order or other names, want the %d names in order", len(sent), len(names))
	}
}

// getCall is one recorded Properties.Get request: the object it asked and the interface and property it named.
type getCall struct {
	path, iface, property string
}

// getCalls returns the Get requests of bus in order.
func getCalls(t *testing.T, bus *fakeBus) []getCall {
	t.Helper()
	var out []getCall
	for _, call := range bus.calls("Get") {
		r := &bodyReader{t: t, order: call.Order, b: call.Body}
		iface := r.str()
		property := r.str()
		out = append(out, getCall{path: call.Path, iface: iface, property: property})
	}
	return out
}

// wantGets checks that bus received exactly the two Gets of each path, ActiveEnterTimestamp then NRestarts.
func wantGets(t *testing.T, bus *fakeBus, paths ...string) {
	t.Helper()
	var want []getCall
	for _, p := range paths {
		want = append(want,
			getCall{path: p, iface: unitIface, property: "ActiveEnterTimestamp"},
			getCall{path: p, iface: serviceIface, property: "NRestarts"})
	}
	if got := getCalls(t, bus); !slices.Equal(got, want) {
		t.Errorf("Get calls = %v, want %v", got, want)
	}
}

// removedWhileRunning answers ListUnitsByNames with one unit whose file is gone while it still runs: not-found and
// active, on the object of the captured mariadb unit (whose Get replies are measured).
func removedWhileRunning() respondFunc {
	entry := unitEntry{
		ID: "gone.service", Description: "gone.service", Load: "not-found", Active: "active", Sub: "running",
		Path: mariadbPath, JobPath: "/",
	}
	return replyWith("ListUnitsByNames", unitListSignature, unitListBody([]unitEntry{entry}))
}

func TestSession_ReadSkipsGetsForNotFoundInactive(t *testing.T) {
	t.Run("not-found and inactive", func(t *testing.T) {
		s, bus, ctx := openSession(t, fakeBusConfig{})

		got, err := s.Read(ctx, []string{"vandox-does-not-exist.service"})

		want := []Unit{{ID: "vandox-does-not-exist.service", LoadState: "not-found", ActiveState: "inactive", SubState: "dead"}}
		if err != nil || !slices.Equal(got, want) {
			t.Errorf("Read(not-found inactive unit) = %v, %v, want %v", got, err, want)
		}
		if gets := getCalls(t, bus); len(gets) != 0 {
			t.Errorf("Read(not-found inactive unit) made Get calls %v, want none", gets)
		}
	})
	t.Run("not-found but still active", func(t *testing.T) {
		s, bus, ctx := openSession(t, fakeBusConfig{Respond: removedWhileRunning()})

		got, err := s.Read(ctx, []string{"gone.service"})

		want := []Unit{{ID: "gone.service", LoadState: "not-found", ActiveState: "active", SubState: "running", ActiveEnter: 1792240371069328}}
		if err != nil || !slices.Equal(got, want) {
			t.Errorf("Read(not-found active unit) = %v, %v, want %v", got, err, want)
		}
		wantGets(t, bus, mariadbPath)
	})
	t.Run("loaded and active", func(t *testing.T) {
		s, bus, ctx := openSession(t, fakeBusConfig{})

		got, err := s.Read(ctx, []string{"mariadb.service"})

		want := []Unit{{ID: "mariadb.service", LoadState: "loaded", ActiveState: "active", SubState: "running", ActiveEnter: 1792240371069328}}
		if err != nil || !slices.Equal(got, want) {
			t.Errorf("Read(mariadb.service) = %v, %v, want %v", got, err, want)
		}
		wantGets(t, bus, mariadbPath)
	})
}

func TestSession_ReadResolvesAliasesInChunks(t *testing.T) {
	s, bus, ctx := openSession(t, fakeBusConfig{})
	names := make([]string, 66)
	wantIDsInOrder := make([]string, len(names))
	for i := range names {
		names[i] = fmt.Sprintf("unit%03d.service", i)
		wantIDsInOrder[i] = names[i]
	}
	for _, i := range []int{0, 65} {
		names[i] = "mysql.service" // an alias of mariadb.service, once in each chunk
		wantIDsInOrder[i] = "mariadb.service"
	}

	got, err := s.Read(ctx, names)

	if err != nil {
		t.Fatalf("Read(%d names) = %v, want no error", len(names), err)
	}
	ids := make([]string, len(got))
	for i, u := range got {
		ids[i] = u.ID
	}
	if !slices.Equal(ids, wantIDsInOrder) {
		t.Errorf("Read(%d names) returned ids %q, want one Unit per name in order, an alias under its primary name %q", len(names), ids, "mariadb.service")
	}
	wantNames(t, bus, []int{64, 2}, names)
	for _, i := range []int{0, 65} {
		if i < len(got) && (got[i].ActiveEnter != 1792240371069328 || got[i].LoadState != "loaded") {
			t.Errorf("Read(...)[%d] = %+v, want the loaded mariadb unit with its active_enter", i, got[i])
		}
	}
	wantGets(t, bus, mariadbPath, mariadbPath)
}

func TestSession_ListReadsNoProperties(t *testing.T) {
	s, bus, ctx := openSession(t, fakeBusConfig{})

	got, err := s.List(ctx, capturedPatterns)

	want := []Unit{{ID: wantPlesk.ID, LoadState: wantPlesk.LoadState, ActiveState: wantPlesk.ActiveState, SubState: wantPlesk.SubState}}
	if err != nil || !slices.Equal(got, want) {
		t.Errorf("List(%q) = %v, %v, want %v", capturedPatterns, got, err, want)
	}
	if calls := bus.calls("ListUnitsByPatterns"); len(calls) != 1 {
		t.Errorf("List made %d ListUnitsByPatterns calls, want 1", len(calls))
	}
	if gets := getCalls(t, bus); len(gets) != 0 {
		t.Errorf("List made Get calls %v, want none (ActiveEnter and Restarts are not read)", gets)
	}
}

func TestSession_ListUnitsByNamesRefusesWrongEntryCount(t *testing.T) {
	names := []string{"a.service", "b.service", "c.service"}
	entries := func(n int) []unitEntry {
		out := make([]unitEntry, n)
		for i := range out {
			out[i] = stubEntry(fmt.Sprintf("x%d.service", i))
		}
		return out
	}
	for _, n := range []int{0, 2, 4} {
		t.Run(fmt.Sprintf("%d entries for 3 names", n), func(t *testing.T) {
			s, _, ctx := openSession(t, fakeBusConfig{Respond: replyWith("ListUnitsByNames", "a(ssssssouso)", unitListBody(entries(n)))})

			got, err := s.ListUnitsByNames(ctx, names)

			wantErrorPrefix(t, "ListUnitsByNames", err, "sdbus: ListUnitsByNames: ")
			if len(got) != 0 {
				t.Errorf("ListUnitsByNames returned %d entries with its error, want none", len(got))
			}
		})
	}
}

// pleskBody is the valid reply body of one unit.
func pleskBody() []byte {
	return unitListBody([]unitEntry{{ID: wantPlesk.ID, Description: "d", Load: "loaded", Active: "active", Sub: "running", Path: pleskPath, JobPath: "/"}})
}

func TestSession_RefusesMalformedBody(t *testing.T) {
	entry := func(mutate func(*unitEntry)) []byte {
		e := unitEntry{ID: wantPlesk.ID, Description: "d", Load: "loaded", Active: "active", Sub: "running", Path: pleskPath, JobPath: "/"}
		mutate(&e)
		return unitListBody([]unitEntry{e})
	}
	patch := func(mutate func(b []byte)) []byte {
		b := pleskBody()
		mutate(b)
		return b
	}
	cases := []struct {
		name string
		body []byte
	}{
		{"invalid UTF-8 in the id", entry(func(e *unitEntry) { e.ID = "plesk\xff.service" })},
		{"id without its NUL", patch(func(b []byte) { b[12+len(wantPlesk.ID)] = 'x' })},
		{"string longer than the body", patch(func(b []byte) { binary.LittleEndian.PutUint32(b[8:], 1<<20) })},
		{"array longer than the body", patch(func(b []byte) { binary.LittleEndian.PutUint32(b, toU32(len(b))) })},
		{"truncated entry", func() []byte {
			b := pleskBody()[:len(pleskBody())-4]
			binary.LittleEndian.PutUint32(b, toU32(len(b)-8))
			return b
		}()},
		{"empty object path", entry(func(e *unitEntry) { e.Path = "" })},
		{"relative object path", entry(func(e *unitEntry) { e.Path = "org/freedesktop/systemd1" })},
		{"object path with an empty element", entry(func(e *unitEntry) { e.Path = "/org//systemd1" })},
		{"object path with a trailing slash", entry(func(e *unitEntry) { e.Path = "/org/systemd1/" })},
		{"object path with a dash", entry(func(e *unitEntry) { e.Path = "/org/system-d1" })},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			s, _, ctx := openSession(t, fakeBusConfig{Respond: replyWith("ListUnitsByPatterns", "a(ssssssouso)", c.body)})

			got, err := s.ListUnitsByPatterns(ctx, capturedPatterns)

			wantError(t, "ListUnitsByPatterns", err, "sdbus: ListUnitsByPatterns: malformed body")
			if len(got) != 0 {
				t.Errorf("ListUnitsByPatterns returned %d entries with its error, want none", len(got))
			}
		})
	}
}

func TestSession_RefusesUnexpectedSignature(t *testing.T) {
	str := newWire(binary.LittleEndian)
	str.str("x")
	enter32 := func(ctx context.Context, s *Session) error {
		_, err := s.GetUint32(ctx, mariadbPath, unitIface, "ActiveEnterTimestamp")
		return err
	}
	restarts64 := func(ctx context.Context, s *Session) error {
		_, err := s.GetUint64(ctx, mariadbPath, serviceIface, "NRestarts")
		return err
	}
	cases := []struct {
		name    string
		respond respondFunc
		call    func(ctx context.Context, s *Session) error
		want    []string
	}{
		{"units listing answered with a string", replyWith("ListUnitsByPatterns", "s", str.b), patternsCall, []string{"sdbus: ListUnitsByPatterns: unexpected signature"}},
		{"units listing with one more string per entry", replyWith("ListUnitsByPatterns", "a(sssssssouso)", pleskBody()), patternsCall, []string{"sdbus: ListUnitsByPatterns: unexpected signature"}},
		{"units listing without a signature", replyWith("ListUnitsByPatterns", "", nil), patternsCall, []string{"sdbus: ListUnitsByPatterns: unexpected signature"}},
		{"names listing answered with a string array", replyWith("ListUnitsByNames", "as", []byte{0, 0, 0, 0}), namesCall, []string{"sdbus: ListUnitsByNames: unexpected signature"}},
		{"property answered with a string", replyWith("Get", "s", str.b), restartsCall, []string{"sdbus: Get: unexpected signature"}},
		{"uint64 property read as uint32", nil, enter32, []string{"sdbus: Get: unexpected signature", "sdbus: Get: malformed body"}},
		{"uint32 property read as uint64", nil, restarts64, []string{"sdbus: Get: unexpected signature", "sdbus: Get: malformed body"}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			s, _, ctx := openSession(t, fakeBusConfig{Respond: c.respond})

			err := c.call(ctx, s)

			if err == nil || !slices.Contains(c.want, err.Error()) {
				t.Errorf("error = %v, want one of %q", err, c.want)
			}
		})
	}
}

func TestSession_ErrorReplyFailsTheCall(t *testing.T) {
	const leak = "text-of-the-reply-7f3a"
	longName := func(n int) string { return "org." + strings.Repeat("a", n-len("org.")) }
	const patterns, names, get = "ListUnitsByPatterns", "ListUnitsByNames", "Get"
	cases := []struct {
		name   string
		error  string
		member string
		call   func(ctx context.Context, s *Session) error
		want   string
	}{
		{"access denied", "org.freedesktop.DBus.Error.AccessDenied", patterns, patternsCall, "sdbus: ListUnitsByPatterns: error reply org.freedesktop.DBus.Error.AccessDenied"},
		{"error of a property read", "org.freedesktop.DBus.Error.UnknownObject", get, restartsCall, "sdbus: Get: error reply org.freedesktop.DBus.Error.UnknownObject"},
		{"error of a names listing", "org.freedesktop.DBus.Error.NoMemory", names, namesCall, "sdbus: ListUnitsByNames: error reply org.freedesktop.DBus.Error.NoMemory"},
		{"name of 255 bytes", longName(255), patterns, patternsCall, "sdbus: ListUnitsByPatterns: error reply " + longName(255)},
		{"name of 256 bytes", longName(256), patterns, patternsCall, "sdbus: ListUnitsByPatterns: error reply"},
		{"name outside the grammar", "not an error name\nwith a line end", patterns, patternsCall, "sdbus: ListUnitsByPatterns: error reply"},
		{"name with one element", "NoDot", patterns, patternsCall, "sdbus: ListUnitsByPatterns: error reply"},
		{"empty name", "", patterns, patternsCall, "sdbus: ListUnitsByPatterns: error reply"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			respond := onMember(c.member, func(req busMessage) []frame {
				return []frame{errorFrame(req.Serial, c.error, leak)}
			})
			s, _, ctx := openSession(t, fakeBusConfig{Respond: respond})

			err := c.call(ctx, s)

			wantError(t, "call", err, c.want)
			if err != nil && strings.Contains(err.Error(), leak) {
				t.Errorf("error %q holds the text of the reply body", err)
			}
		})
	}
}

// failingMember answers the calls of member with an error reply; for Get, only those that ask for property (any when
// empty). The reply is hand-built, hostile input: the captured bus never answered these calls with an error.
func failingMember(t *testing.T, member, property, text string) respondFunc {
	return func(req busMessage) ([]frame, bool) {
		if req.Member != member {
			return nil, false
		}
		if property != "" {
			r := &bodyReader{t: t, order: req.Order, b: req.Body}
			_ = r.str()
			if r.str() != property {
				return nil, false
			}
		}
		return []frame{errorFrame(req.Serial, "org.freedesktop.DBus.Error.NoMemory", text)}, true
	}
}

// askedProperties returns the properties of the Get requests of bus in order.
func askedProperties(t *testing.T, bus *fakeBus) []string {
	t.Helper()
	var asked []string
	for _, g := range getCalls(t, bus) {
		asked = append(asked, g.property)
	}
	return asked
}

func TestSession_ReadFailsOnAnErrorReply(t *testing.T) {
	const leak = "text-of-the-reply-5c2e"
	cases := []struct {
		name     string
		member   string
		property string
		want     string
		gets     []string
	}{
		{"error of the list", "ListUnitsByNames", "", "sdbus: ListUnitsByNames: error reply org.freedesktop.DBus.Error.NoMemory", nil},
		{"error of the first Get", "Get", "ActiveEnterTimestamp", "sdbus: Get: error reply org.freedesktop.DBus.Error.NoMemory", []string{"ActiveEnterTimestamp"}},
		{"error of the second Get", "Get", "NRestarts", "sdbus: Get: error reply org.freedesktop.DBus.Error.NoMemory", []string{"ActiveEnterTimestamp", "NRestarts"}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			s, bus, ctx := openSession(t, fakeBusConfig{Respond: failingMember(t, c.member, c.property, leak)})

			got, err := s.Read(ctx, []string{"mariadb.service"})

			wantError(t, "Read", err, c.want)
			if len(got) != 0 {
				t.Errorf("Read returned %d units with its error, want none", len(got))
			}
			if asked := askedProperties(t, bus); !slices.Equal(asked, c.gets) {
				t.Errorf("Read asked for the properties %v, want %v (it stops at the first error)", asked, c.gets)
			}
		})
	}
}

func TestSession_ListFailsOnAnErrorReply(t *testing.T) {
	// A hand-built error reply (hostile input): the captured bus never answered this call with an error.
	respond := onMember("ListUnitsByPatterns", func(req busMessage) []frame {
		return []frame{errorFrame(req.Serial, "org.freedesktop.DBus.Error.AccessDenied", "text")}
	})
	s, _, ctx := openSession(t, fakeBusConfig{Respond: respond})

	got, err := s.List(ctx, capturedPatterns)

	wantError(t, "List", err, "sdbus: ListUnitsByPatterns: error reply org.freedesktop.DBus.Error.AccessDenied")
	if len(got) != 0 {
		t.Errorf("List returned %d units with its error, want none", len(got))
	}
}

func TestSession_RefusesOversizedReply(t *testing.T) {
	t.Run("body over the reply bound", func(t *testing.T) {
		body := make([]byte, MaxReplyBody+1)
		s, _, ctx := openSession(t, fakeBusConfig{Respond: replyWith("ListUnitsByPatterns", "a(ssssssouso)", body)})

		err := patternsCall(ctx, s)

		wantError(t, "ListUnitsByPatterns", err, "sdbus: ListUnitsByPatterns: reply too large")
	})
	t.Run("header fields over the bound", func(t *testing.T) {
		spec := msgSpec{Type: typeMethodReturn, Serial: 5, Fields: []headerField{
			{Code: fieldInterface, Sig: "s", Value: strings.Repeat("a", MaxHeaderFields)},
		}}
		s, _, ctx := openSession(t, fakeBusConfig{Respond: onMember("ListUnitsByPatterns", func(req busMessage) []frame {
			spec.Fields = append(spec.Fields, headerField{Code: fieldReplySerial, Sig: "u", Value: req.Serial})
			return []frame{rawFrame(spec.bytes())}
		})})

		err := patternsCall(ctx, s)

		wantErrorPrefix(t, "ListUnitsByPatterns", err, "sdbus: ")
	})
	t.Run("message over the protocol maximum", func(t *testing.T) {
		huge := msgSpec{Type: typeSignal, Serial: 5, Fields: signalFields("/org/example", "org.example.Flood", "Burst"), BodyLen: MaxMessage}
		s, _, ctx := openSession(t, fakeBusConfig{Preamble: func(busMessage) []frame { return []frame{rawFrame(huge.header())} }})

		err := patternsCall(ctx, s)

		wantErrorPrefix(t, "ListUnitsByPatterns", err, "sdbus: message: ")
	})
}

func TestSession_RefusesMalformedFixedHeader(t *testing.T) {
	cases := []struct {
		name string
		spec msgSpec
	}{
		{"unknown byte order", msgSpec{Order: 'x', Type: typeSignal, Serial: 5}},
		{"protocol version 2", msgSpec{Version: 2, Type: typeSignal, Serial: 5, Fields: signalFields("/a", "a.b", "C")}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			raw := c.spec.bytes()
			if c.spec.Order == 'x' {
				raw = raw[:16] // the reader cannot know how long the rest is
			}
			s, _, ctx := openSession(t, fakeBusConfig{Preamble: func(busMessage) []frame { return []frame{rawFrame(raw)} }})

			err := patternsCall(ctx, s)

			wantErrorPrefix(t, "ListUnitsByPatterns", err, "sdbus: message: ")
		})
	}
}

func TestSession_RefusesUnknownHeaderFieldType(t *testing.T) {
	variantString := newWire(binary.LittleEndian)
	variantString.sig("s")
	variantString.str("x")
	emptyArray := []byte{0, 0, 0, 0}
	cases := []struct {
		name  string
		extra headerField
	}{
		{"sender typed as uint32", headerField{Code: fieldSender, Sig: "u", Value: uint32(1)}},
		{"path typed as uint32", headerField{Code: fieldPath, Sig: "u", Value: uint32(1)}},
		{"signature typed as string", headerField{Code: fieldSignature, Sig: "s", Value: "s"}},
		{"unix fds typed as string", headerField{Code: fieldUnixFDs, Sig: "s", Value: "x"}},
		{"unknown code 10 with a uint32", headerField{Code: 10, Sig: "u", Value: uint32(1)}},
		{"unknown code 10 with a string", headerField{Code: 10, Sig: "s", Value: "x"}},
		{"invalid code 0", headerField{Code: 0, Sig: "u", Value: uint32(1)}},
		{"variant inside the variant", headerField{Code: fieldDestination, Sig: "v", Value: variantString.b, Align: 1}},
		{"array of strings", headerField{Code: fieldDestination, Sig: "as", Value: emptyArray, Align: 4}},
		{"dictionary array", headerField{Code: 99, Sig: "a{sv}", Value: emptyArray, Align: 4}},
		{"empty signature", headerField{Code: fieldDestination, Sig: ""}},
	}
	types := []byte{typeMethodReturn, typeError}
	for _, c := range cases {
		for _, typ := range types {
			t.Run(fmt.Sprintf("%s in message type %d", c.name, typ), func(t *testing.T) {
				respond := onMember("ListUnitsByPatterns", func(req busMessage) []frame {
					fields := []headerField{{Code: fieldReplySerial, Sig: "u", Value: req.Serial}, c.extra}
					return []frame{rawFrame(msgSpec{Type: typ, Flags: flagNoReplyExpected, Serial: 9, Fields: fields}.bytes())}
				})
				s, _, ctx := openSession(t, fakeBusConfig{Respond: respond})

				err := patternsCall(ctx, s)

				wantErrorPrefix(t, "ListUnitsByPatterns", err, "sdbus: message: ")
			})
		}
	}
}

// unsolicited returns the messages a client may find between its request and the reply.
func unsolicited(t *testing.T) map[string]func(req busMessage) frame {
	t.Helper()
	c := loadCapture(t)
	hello := loadHello(t)
	bigEndian := msgSpec{Order: 'B', Type: typeSignal, Flags: flagNoReplyExpected, Serial: 300, Fields: signalFields("/org/example", "org.example.Big", "Endian"), Body: []byte("big endian body")}
	oversizedFields := msgSpec{Type: typeSignal, Serial: 301, RawFields: make([]byte, 100<<10), Body: []byte("fields over the bound")}
	notDecodable := headerField{Code: 99, Sig: "a{sv}", Value: []byte{0, 0, 0, 0}, Align: 4}
	badFieldType := msgSpec{Type: typeSignal, Serial: 302, Fields: append(signalFields("/a", "a.b", "C"), notDecodable), Body: []byte("x")}
	str := newWire(binary.LittleEndian)
	str.str("another reply")
	return map[string]func(req busMessage) frame{
		"captured UnitNew":           func(busMessage) frame { return rawFrame(c.at(19).Raw) },
		"captured UnitRemoved":       func(busMessage) frame { return rawFrame(c.at(20).Raw) },
		"captured PropertiesChanged": func(busMessage) frame { return rawFrame(c.at(73).Raw) },
		"larger PropertiesChanged":   func(busMessage) frame { return rawFrame(c.at(74).Raw) },
		"measured NameAcquired":      func(busMessage) frame { return rawFrame(hello.nameAcquired) },
		"message of unknown type 5": func(busMessage) frame {
			return rawFrame(msgSpec{Type: 5, Serial: 303, Body: []byte("unknown")}.bytes())
		},
		"message of type 200": func(busMessage) frame {
			return rawFrame(msgSpec{Type: 200, Serial: 304, Body: []byte("unknown")}.bytes())
		},
		"method call to the client": func(busMessage) frame {
			fields := []headerField{
				{Code: fieldPath, Sig: "o", Value: "/"}, {Code: fieldInterface, Sig: "s", Value: "org.freedesktop.DBus.Peer"},
				{Code: fieldMember, Sig: "s", Value: "Ping"}, {Code: fieldDestination, Sig: "s", Value: ":1.1"},
			}
			return rawFrame(msgSpec{Type: typeMethodCall, Serial: 305, Fields: fields}.bytes())
		},
		"big-endian signal":                  func(busMessage) frame { return rawFrame(bigEndian.bytes()) },
		"signal with header fields over 64K": func(busMessage) frame { return rawFrame(oversizedFields.bytes()) },
		"signal with an undecodable field":   func(busMessage) frame { return rawFrame(badFieldType.bytes()) },
		"method return of another call": func(req busMessage) frame {
			return methodReturnFrame(req.Serial+100, "s", str.b)
		},
		"error reply of another call": func(req busMessage) frame {
			return errorFrame(req.Serial+100, "org.freedesktop.DBus.Error.Failed", "another")
		},
	}
}

func TestSession_SkipsUnsolicitedMessages(t *testing.T) {
	messages := unsolicited(t)
	names := make([]string, 0, len(messages))
	for name := range messages {
		names = append(names, name)
	}
	slices.Sort(names)
	for _, name := range names {
		t.Run(name, func(t *testing.T) {
			preamble := func(req busMessage) []frame { return []frame{messages[name](req)} }
			s, _, ctx := openSession(t, fakeBusConfig{Preamble: preamble})

			got, err := s.ListUnitsByPatterns(ctx, capturedPatterns)

			if err != nil || !slices.Equal(got, []UnitInfo{wantPlesk}) {
				t.Errorf("ListUnitsByPatterns after %q = %v, %v, want %v", name, got, err, []UnitInfo{wantPlesk})
			}
		})
	}
	t.Run("all of them in a row", func(t *testing.T) {
		preamble := func(req busMessage) []frame {
			var all []frame
			for _, name := range names {
				all = append(all, messages[name](req))
			}
			return all
		}
		s, _, ctx := openSession(t, fakeBusConfig{Preamble: preamble})

		got, err := s.ListUnitsByPatterns(ctx, capturedPatterns)

		if err != nil || !slices.Equal(got, []UnitInfo{wantPlesk}) {
			t.Errorf("ListUnitsByPatterns after %d unsolicited messages = %v, %v, want %v", len(names), got, err, []UnitInfo{wantPlesk})
		}
	})
}

func TestSession_DiscardsUnsolicitedSignalsWithoutKeepingThem(t *testing.T) {
	const signals, declaredBody = 8, 32 << 20
	flood := hugeSignal(declaredBody)
	frames := make([]frame, signals)
	for i := range frames {
		frames[i] = flood
	}
	s, _, ctx := openSession(t, fakeBusConfig{Preamble: func(busMessage) []frame { return frames }})
	var before, after runtime.MemStats

	runtime.ReadMemStats(&before)
	got, err := s.ListUnitsByPatterns(ctx, capturedPatterns)
	runtime.ReadMemStats(&after)

	if err != nil || !slices.Equal(got, []UnitInfo{wantPlesk}) {
		t.Fatalf("ListUnitsByPatterns after %d signals of %d MiB = %v, %v, want %v", signals, declaredBody>>20, got, err, []UnitInfo{wantPlesk})
	}
	if grown := after.TotalAlloc - before.TotalAlloc; grown >= 1<<20 {
		t.Errorf("the call allocated %d bytes while discarding %d signals of %d MiB, want less than 1 MiB", grown, signals, declaredBody>>20)
	}
}

// callShape is what the tests pin of a request: where it goes and how.
type callShape struct {
	destination, path, iface, member, signature string
	flags                                       byte
}

func TestSession_CallsOnlyReadMethods(t *testing.T) {
	s, bus, ctx := openSession(t, fakeBusConfig{})

	_, listErr := s.ListUnitsByPatterns(ctx, capturedPatterns)
	_, namesErr := s.ListUnitsByNames(ctx, []string{"mariadb.service"})
	_, enterErr := s.GetUint64(ctx, mariadbPath, unitIface, "ActiveEnterTimestamp")
	_, restartsErr := s.GetUint32(ctx, mariadbPath, serviceIface, "NRestarts")

	if err := errors.Join(listErr, namesErr, enterErr, restartsErr); err != nil {
		t.Fatalf("the five calls failed: %v", err)
	}
	wantAuth := []string{"AUTH EXTERNAL " + hex.EncodeToString([]byte(strconv.Itoa(testUID))) + "\r\n", "BEGIN\r\n"}
	if got := bus.authentication(); !slices.Equal(got, wantAuth) {
		t.Errorf("authentication lines = %q, want %q (no NEGOTIATE_UNIX_FD)", got, wantAuth)
	}
	want := []callShape{
		{busName, "/org/freedesktop/DBus", busName, "Hello", "", 0},
		{systemdName, managerPath, managerIface, "ListUnitsByPatterns", "asas", flagNoAutoStart},
		{systemdName, managerPath, managerIface, "ListUnitsByNames", "as", flagNoAutoStart},
		{systemdName, mariadbPath, propsIface, "Get", "ss", flagNoAutoStart},
		{systemdName, mariadbPath, propsIface, "Get", "ss", flagNoAutoStart},
	}
	requests := bus.recorded()
	shapes := make([]callShape, len(requests))
	for i, req := range requests {
		shapes[i] = callShape{req.Destination, req.Path, req.Interface, req.Member, req.Signature, req.Flags}
	}
	if !slices.Equal(shapes, want) {
		t.Errorf("requests = %+v, want %+v", shapes, want)
	}
	wantSerialsIncrease(t, requests)
	wantGetArguments(t, requests[3:])
}

// wantSerialsIncrease checks that the serials are not zero and strictly increase.
func wantSerialsIncrease(t *testing.T, requests []busMessage) {
	t.Helper()
	var last uint32
	for _, req := range requests {
		if req.Serial <= last {
			t.Errorf("request %s has serial %d after %d, want a nonzero, increasing serial", req.Member, req.Serial, last)
		}
		last = req.Serial
	}
}

// wantGetArguments checks the interface and property of the two Get calls.
func wantGetArguments(t *testing.T, gets []busMessage) {
	t.Helper()
	want := [][2]string{{unitIface, "ActiveEnterTimestamp"}, {serviceIface, "NRestarts"}}
	for i, get := range gets {
		r := &bodyReader{t: t, order: get.Order, b: get.Body}
		got := [2]string{r.str(), r.str()}
		if got != want[i] {
			t.Errorf("Get call %d asked for %q, want %q", i, got, want[i])
		}
	}
}

func TestDial_ConnectsAndAuthenticates(t *testing.T) {
	ctx := testContext(t)
	bus := newFakeBus(t, fakeBusConfig{})
	path := bus.listen()

	s, err := Dial(ctx, path)
	if err != nil {
		t.Fatalf("Dial(%q) = %v, want a session", path, err)
	}
	got, listErr := s.ListUnitsByPatterns(ctx, capturedPatterns)
	closeErr := s.Close()
	_, afterErr := s.ListUnitsByPatterns(ctx, capturedPatterns)

	if listErr != nil || !slices.Equal(got, []UnitInfo{wantPlesk}) {
		t.Errorf("ListUnitsByPatterns = %v, %v, want %v", got, listErr, []UnitInfo{wantPlesk})
	}
	wantAuth := "AUTH EXTERNAL " + hex.EncodeToString([]byte(strconv.Itoa(os.Getuid()))) + "\r\n"
	if lines := bus.authentication(); len(lines) == 0 || lines[0] != wantAuth {
		t.Errorf("authentication lines = %q, want first %q", lines, wantAuth)
	}
	if closeErr != nil {
		t.Errorf("Close() = %v, want nil", closeErr)
	}
	bus.waitClosed()
	if afterErr == nil {
		t.Error("a call after Close succeeded, want an error")
	}
}

func TestDial_ReportsAnUnreachableBus(t *testing.T) {
	missing := filepath.Join(t.TempDir(), "no-such-socket")

	s, err := Dial(testContext(t), missing)

	wantErrorPrefix(t, "Dial", err, "sdbus: connect: ")
	if err != nil && !strings.Contains(err.Error(), "no such file or directory") {
		t.Errorf("Dial error = %q, want the operating system's reason", err)
	}
	if s != nil {
		t.Errorf("Dial returned a session %v with its error, want nil", s)
	}
}

func TestOpen_RefusesUnexpectedAuthReply(t *testing.T) {
	const guid = "379ab99b4e3b5df5bce269566ad7ae12"
	lines := map[string]frame{
		"rejected":                  rawFrame([]byte("REJECTED EXTERNAL\r\n")),
		"OK without a guid":         rawFrame([]byte("OK\r\n")),
		"guid of 31 digits":         rawFrame([]byte("OK " + guid[1:] + "\r\n")),
		"guid of 33 digits":         rawFrame([]byte("OK " + guid + "0\r\n")),
		"guid with a non-hex digit": rawFrame([]byte("OK " + guid[:31] + "z\r\n")),
		"text after the guid":       rawFrame([]byte("OK " + guid + " x\r\n")),
		"error":                     rawFrame([]byte("ERROR\r\n")),
		"data request":              rawFrame([]byte("DATA 00\r\n")),
		"file descriptor agreement": rawFrame([]byte("AGREE_UNIX_FD\r\n")),
		"line without an end":       {raw: bytes.Repeat([]byte("A"), 600), streamed: 1 << 20},
	}
	for name, reply := range lines {
		t.Run(name, func(t *testing.T) {
			bus := newFakeBus(t, fakeBusConfig{AuthReply: reply})

			_, err := Open(testContext(t), bus.pipe(), testUID)

			wantError(t, "Open", err, "sdbus: authentication refused")
		})
	}
}

func TestOpen_HelloErrorFailsTheSession(t *testing.T) {
	respond := onMember("Hello", func(req busMessage) []frame {
		return []frame{errorFrame(req.Serial, "org.freedesktop.DBus.Error.AccessDenied", "no")}
	})
	bus := newFakeBus(t, fakeBusConfig{Respond: respond})

	_, err := Open(testContext(t), bus.pipe(), testUID)

	wantError(t, "Open", err, "sdbus: Hello: error reply org.freedesktop.DBus.Error.AccessDenied")
}

func TestSession_ContextEndsAHungCall(t *testing.T) {
	silent := fakeBusConfig{Respond: onMember("ListUnitsByPatterns", func(busMessage) []frame { return nil })}
	t.Run("deadline", func(t *testing.T) {
		synctest.Test(t, func(t *testing.T) {
			s, _, ctx := openSession(t, silent)
			ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
			defer cancel()

			err := patternsCall(ctx, s)

			if err == nil || ctx.Err() == nil {
				t.Errorf("call = %v, context error = %v, want an error after the deadline", err, ctx.Err())
			}
		})
	})
	t.Run("cancellation", func(t *testing.T) {
		synctest.Test(t, func(t *testing.T) {
			s, _, ctx := openSession(t, silent)
			ctx, cancel := context.WithCancel(ctx)
			defer cancel()
			time.AfterFunc(2*time.Second, cancel)

			err := patternsCall(ctx, s)

			if err == nil || ctx.Err() == nil {
				t.Errorf("call = %v, context error = %v, want an error after the cancellation", err, ctx.Err())
			}
		})
	})
}

// allowedImports are the packages the client may import: no unsafe, reflect, syscall, process or HTTP packages.
var allowedImports = []string{
	"bufio", "bytes", "context", "encoding/binary", "encoding/hex", "errors", "fmt", "io", "math", "net", "os",
	"slices", "strconv", "strings", "sync", "sync/atomic", "time", "unicode/utf8",
}

// allowedSelectors maps a package to the only selectors of it the non-test files may use: the connection is only
// net.Conn (made by net.Dialer), the process contributes only its user id, and no environment variable is read.
var allowedSelectors = map[string][]string{
	"net":     {"Conn", "Dialer", "ErrClosed"},
	"os":      {"Getuid", "Geteuid", "ErrDeadlineExceeded"},
	"syscall": {},
}

// forbiddenName reports the names of methods and types that give access to descriptors or to the raw socket, whatever
// their receiver.
func forbiddenName(name string) bool {
	return name == "ReadMsgUnix" || name == "SyscallConn" || name == "File" || strings.HasPrefix(name, "ReadFrom")
}

// restrictedNames returns the local name of every import of file whose path is a key of allowedSelectors, and the
// violations for dot imports of such a package.
func restrictedNames(name string, file *ast.File) (names map[string]string, violations []string) {
	names = map[string]string{}
	for _, imp := range file.Imports {
		path, err := strconv.Unquote(imp.Path.Value)
		if _, restricted := allowedSelectors[path]; err != nil || !restricted {
			continue
		}
		local := path[strings.LastIndex(path, "/")+1:]
		if imp.Name != nil {
			local = imp.Name.Name
		}
		switch local {
		case "_":
		case ".":
			violations = append(violations, name+": dot import of "+path+" is not allowed")
		default:
			names[local] = path
		}
	}
	return names, violations
}

// disallowedSelectors returns the uses in file of selectors that are not allowed: any selector with a forbidden name,
// and the selectors of a restricted package that allowedSelectors does not list.
func disallowedSelectors(name string, file *ast.File) []string {
	names, violations := restrictedNames(name, file)
	ast.Inspect(file, func(n ast.Node) bool {
		sel, ok := n.(*ast.SelectorExpr)
		if !ok {
			return true
		}
		if forbiddenName(sel.Sel.Name) {
			violations = append(violations, name+": "+sel.Sel.Name+" is not allowed")
		}
		id, ok := sel.X.(*ast.Ident)
		if !ok {
			return true
		}
		path, bound := names[id.Name]
		if bound && id.Obj == nil && !slices.Contains(allowedSelectors[path], sel.Sel.Name) {
			violations = append(violations, name+": "+id.Name+"."+sel.Sel.Name+" is not allowed")
		}
		return true
	})
	return violations
}

// disallowedImports returns the imports in file that allowedImports does not list.
func disallowedImports(name string, file *ast.File) []string {
	var violations []string
	for _, imp := range file.Imports {
		path, err := strconv.Unquote(imp.Path.Value)
		if err != nil || !slices.Contains(allowedImports, path) {
			violations = append(violations, name+": imports "+imp.Path.Value+", want only "+strings.Join(allowedImports, " "))
		}
	}
	return violations
}

func TestSdbus_ImportsOnlyAllowedPackages(t *testing.T) {
	files, err := filepath.Glob("*.go")
	if err != nil || len(files) == 0 {
		t.Fatalf("Glob(*.go) = %v, %v, want the package's files", files, err)
	}
	checked := 0
	for _, name := range files {
		if strings.HasSuffix(name, "_test.go") {
			continue
		}
		checked++
		file, err := parser.ParseFile(token.NewFileSet(), name, nil, 0)
		if err != nil {
			t.Fatalf("parsing %s: %v", name, err)
		}
		for _, v := range append(disallowedImports(name, file), disallowedSelectors(name, file)...) {
			t.Error(v)
		}
	}
	if checked < 2 {
		t.Errorf("checked %d production files, want the client (sdbus.go) and its message reader and writer (message.go)", checked)
	}
}
