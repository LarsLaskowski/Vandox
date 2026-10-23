package agentstate

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"syscall"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

const (
	warnUnavailable = "agent state not available"
	warnNotSaved    = "agent state not saved"
	bootID          = "0f8fad5b-d9cb-469f-a165-70867728950e"
	seqnumID        = "1f8fad5b-d9cb-469f-a165-70867728950e"
)

var t0 = time.Date(2026, time.October, 20, 12, 0, 0, 0, time.UTC)

// logLine is one captured log record.
type logLine struct {
	Level string `json:"level"`
	Msg   string `json:"msg"`
	Err   string `json:"err"`
}

// logCapture collects what a store logs.
type logCapture struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (c *logCapture) Write(p []byte) (int, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.buf.Write(p)
}

func (c *logCapture) logger() *slog.Logger {
	return slog.New(slog.NewJSONHandler(c, &slog.HandlerOptions{Level: slog.LevelDebug}))
}

func (c *logCapture) lines(t *testing.T) []logLine {
	t.Helper()
	c.mu.Lock()
	defer c.mu.Unlock()
	var out []logLine
	for _, raw := range bytes.Split(bytes.TrimSpace(c.buf.Bytes()), []byte("\n")) {
		if len(raw) == 0 {
			continue
		}
		var l logLine
		if err := json.Unmarshal(raw, &l); err != nil {
			t.Fatalf("log line %q is not JSON: %v", raw, err)
		}
		out = append(out, l)
	}
	return out
}

// count returns the number of lines with msg.
func (c *logCapture) count(t *testing.T, msg string) int {
	t.Helper()
	n := 0
	for _, l := range c.lines(t) {
		if l.Msg == msg {
			n++
		}
	}
	return n
}

// update calls Store.Update and turns the skeleton's panic into a failure.
func update(t *testing.T, s *Store, urgent bool, fn func(*State)) {
	t.Helper()
	defer func() {
		if r := recover(); r != nil {
			t.Fatalf("Update panicked: %v", r)
		}
	}()
	s.Update(urgent, fn)
}

func procFS(uptime string) fstest.MapFS {
	return fstest.MapFS{"uptime": {Data: []byte(uptime + " 4000.00\n")}}
}

func fullState(seq uint64) func(*State) {
	return func(st *State) {
		st.Boot = &Boot{ID: bootID, Uptime: 1000 * time.Second, BootedAt: t0.Add(-1000 * time.Second)}
		st.Journal = &JournalPosition{SeqnumID: seqnumID, Seqnum: seq, Realtime: t0.Add(-time.Minute)}
		st.Backup = &OpenRun{BootID: bootID, StartedAt: t0.Add(-time.Hour), LastSeen: t0.Add(-time.Minute), MaxProcesses: 3}
	}
}

func collect(t *testing.T, sv *Saver, at time.Time) {
	t.Helper()
	recs, err := sv.Collect(context.Background(), scheduler.Run{Start: at})
	if err != nil {
		t.Fatalf("Collect(%v) error = %v, want nil", at, err)
	}
	if len(recs) != 0 {
		t.Fatalf("Collect(%v) returned %d records, want none", at, len(recs))
	}
}

// savedStore returns a directory holding a state file written by the store and the file's path.
func savedStore(t *testing.T) (string, string) {
	t.Helper()
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, fullState(7))
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	path := filepath.Join(dir, FileName)
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("the saved state file is missing: %v", err)
	}
	return dir, path
}

func readFile(t *testing.T, path string) []byte {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("ReadFile(%s) error = %v", path, err)
	}
	return b
}

func dirNames(t *testing.T, dir string) []string {
	t.Helper()
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir error = %v", err)
	}
	var names []string
	for _, e := range entries {
		names = append(names, e.Name())
	}
	return names
}

func TestOpenMissingDirectoryIsUnavailableWithOneWarningAndIsNotCreated(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "spool")
	lc := &logCapture{}
	s, st := Open(dir, lc.logger())
	if s == nil {
		t.Fatal("Open returned a nil store, want a usable one")
	}
	if st != StatusUnavailable {
		t.Errorf("status = %v, want StatusUnavailable", st)
	}
	if n := lc.count(t, warnUnavailable); n != 1 {
		t.Errorf("%q logged %d times, want 1", warnUnavailable, n)
	}
	for _, l := range lc.lines(t) {
		if l.Level != "WARN" {
			t.Errorf("log %q has level %s, want WARN", l.Msg, l.Level)
		}
	}
	if _, err := os.Stat(dir); err == nil {
		t.Error("Open created the directory, want the agent to never create it")
	}
}

func TestOpenEmptyDirectoryIsFirstStartWithoutWarningAndWithoutFile(t *testing.T) {
	dir := t.TempDir()
	lc := &logCapture{}
	_, st := Open(dir, lc.logger())
	if st != StatusFirstStart {
		t.Errorf("status = %v, want StatusFirstStart", st)
	}
	if got := lc.lines(t); len(got) != 0 {
		t.Errorf("logged %v, want nothing on a first start", got)
	}
	if names := dirNames(t, dir); len(names) != 0 {
		t.Errorf("Open left %v in the directory, want it empty", names)
	}
}

func TestOpenAbsentFileBesideOtherEntryIsUnavailable(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "spool-000001.dat"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	lc := &logCapture{}
	_, st := Open(dir, lc.logger())
	if st != StatusUnavailable {
		t.Errorf("status = %v, want StatusUnavailable", st)
	}
	if n := lc.count(t, warnUnavailable); n != 1 {
		t.Errorf("%q logged %d times, want 1", warnUnavailable, n)
	}
}

func TestOpenDirectoryIsAFileIsUnavailable(t *testing.T) {
	file := filepath.Join(t.TempDir(), "notadir")
	if err := os.WriteFile(file, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	lc := &logCapture{}
	_, st := Open(file, lc.logger())
	if st != StatusUnavailable {
		t.Errorf("status = %v, want StatusUnavailable", st)
	}
	if n := lc.count(t, warnUnavailable); n != 1 {
		t.Errorf("%q logged %d times, want 1", warnUnavailable, n)
	}
}

func TestOpenSavedFileIsLoadedWithoutWarning(t *testing.T) {
	dir, _ := savedStore(t)
	lc := &logCapture{}
	s, st := Open(dir, lc.logger())
	if st != StatusLoaded {
		t.Fatalf("status = %v, want StatusLoaded", st)
	}
	if got := lc.lines(t); len(got) != 0 {
		t.Errorf("logged %v, want nothing for a loaded state", got)
	}
	got := s.State()
	if got.Boot == nil || got.Journal == nil || got.Backup == nil {
		t.Fatalf("State() = %+v, want Boot, Journal and Backup set", got)
	}
	if got.Boot.ID != bootID || got.Boot.Uptime != 1000*time.Second || !got.Boot.BootedAt.Equal(t0.Add(-1000*time.Second)) {
		t.Errorf("Boot = %+v, want id %s, uptime 1000s, booted at %v", *got.Boot, bootID, t0.Add(-1000*time.Second))
	}
	if got.Journal.SeqnumID != seqnumID || got.Journal.Seqnum != 7 || !got.Journal.Realtime.Equal(t0.Add(-time.Minute)) {
		t.Errorf("Journal = %+v, want seqnum id %s, seqnum 7, realtime %v", *got.Journal, seqnumID, t0.Add(-time.Minute))
	}
	if got.Backup.BootID != bootID || got.Backup.MaxProcesses != 3 || !got.Backup.StartedAt.Equal(t0.Add(-time.Hour)) || !got.Backup.LastSeen.Equal(t0.Add(-time.Minute)) {
		t.Errorf("Backup = %+v, want the saved run", *got.Backup)
	}
}

func TestOpenPartialStateKeepsAbsentPartsNil(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, func(st *State) {
		st.Journal = &JournalPosition{SeqnumID: seqnumID, Seqnum: 1, Realtime: t0}
	})
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	s2, st := Open(dir, slog.New(slog.DiscardHandler))
	if st != StatusLoaded {
		t.Fatalf("status = %v, want StatusLoaded", st)
	}
	got := s2.State()
	if got.Journal == nil || got.Boot != nil || got.Backup != nil {
		t.Errorf("State() = %+v, want only Journal set", got)
	}
}

// setMode sets the mode bits of path.
func setMode(t *testing.T, path string, mode os.FileMode) {
	t.Helper()
	if err := os.Chmod(path, mode); err != nil {
		t.Fatal(err)
	}
}

// mutateJSON reads the state file, lets fn change the decoded object and writes it back with mode 0600.
func mutateJSON(t *testing.T, path string, fn func(map[string]any)) {
	t.Helper()
	var obj map[string]any
	if err := json.Unmarshal(readFile(t, path), &obj); err != nil {
		t.Fatalf("the state file is not a JSON object: %v", err)
	}
	fn(obj)
	b, err := json.Marshal(obj)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, b, 0o600); err != nil {
		t.Fatal(err)
	}
	setMode(t, path, 0o600)
}

var (
	uuidPattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
	timePattern = regexp.MustCompile(`^\d{4}-\d\d-\d\dT`)
)

// rewriteValues replaces every value of v below the top level for which swap answers true and reports whether one was replaced.
func rewriteValues(v any, swap func(key string, val any) (any, bool)) bool {
	obj, ok := v.(map[string]any)
	if !ok {
		return false
	}
	done := false
	for k, e := range obj {
		if r, hit := swap(k, e); hit {
			obj[k] = r
			done = true
		}
		if rewriteValues(e, swap) {
			done = true
		}
	}
	return done
}

// rewriteStrings replaces every string value of v for which match is true and reports whether one was replaced.
func rewriteStrings(v any, match func(string) bool, with string) bool {
	return rewriteValues(v, func(_ string, val any) (any, bool) {
		s, ok := val.(string)
		return with, ok && match(s)
	})
}

// rewriteNumbers replaces every number below the top level of obj by a string.
func rewriteNumbers(v any) bool {
	return rewriteValues(v, func(k string, val any) (any, bool) {
		_, ok := val.(float64)
		return "7", ok && k != "version"
	})
}

type corruptCase struct {
	name  string
	setup func(t *testing.T, dir, path string)
}

func garbageFile(content string) func(t *testing.T, dir, path string) {
	return func(t *testing.T, _, path string) {
		t.Helper()
		if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
	}
}
func mutateFile(fn func(t *testing.T, obj map[string]any)) func(t *testing.T, dir, path string) {
	return func(t *testing.T, _, path string) {
		t.Helper()
		mutateJSON(t, path, func(obj map[string]any) { fn(t, obj) })
	}
}

var corruptCases = []corruptCase{
	{"GarbageBytes", garbageFile("\x00\x01 not json")},
	{"EmptyFile", garbageFile("")},
	{"TruncatedJSON", func(t *testing.T, _, path string) {
		b := readFile(t, path)
		if err := os.WriteFile(path, b[:len(b)/2], 0o600); err != nil {
			t.Fatal(err)
		}
	}},
	{"TrailingGarbage", func(t *testing.T, _, path string) {
		b := append(readFile(t, path), []byte(` {"x":1}`)...)
		if err := os.WriteFile(path, b, 0o600); err != nil {
			t.Fatal(err)
		}
	}},
	{"JSONArray", garbageFile("[]")},
	{"UnknownTopLevelKey", mutateFile(func(_ *testing.T, obj map[string]any) { obj["unexpected"] = 1 })},
	{"UnknownNestedKey", mutateFile(func(t *testing.T, obj map[string]any) {
		for _, v := range obj {
			if m, ok := v.(map[string]any); ok {
				m["unexpected"] = 1
				return
			}
		}
		t.Fatal("the state file holds no nested object to add a key to")
	})},
	{"VersionTwo", mutateFile(func(_ *testing.T, obj map[string]any) { obj["version"] = 2 })},
	{"VersionMissing", mutateFile(func(_ *testing.T, obj map[string]any) { delete(obj, "version") })},
	{"VersionAsString", mutateFile(func(_ *testing.T, obj map[string]any) { obj["version"] = "1" })},
	{"NumberAsString", mutateFile(func(t *testing.T, obj map[string]any) {
		if !rewriteNumbers(obj) {
			t.Fatal("the state file holds no number besides the version")
		}
	})},
	{"InvalidUUID", mutateFile(func(t *testing.T, obj map[string]any) {
		if !rewriteStrings(obj, uuidPattern.MatchString, "not-a-uuid") {
			t.Fatal("the state file holds no UUID")
		}
	})},
	{"UpperCaseUUID", mutateFile(func(t *testing.T, obj map[string]any) {
		if rewriteStrings(obj, uuidPattern.MatchString, strings.ToUpper(bootID)) == false {
			t.Fatal("the state file holds no UUID")
		}
	})},
	{"InvalidTime", mutateFile(func(t *testing.T, obj map[string]any) {
		if !rewriteStrings(obj, timePattern.MatchString, "yesterday") {
			t.Fatal("the state file holds no time")
		}
	})},
	{"GroupReadable", func(t *testing.T, _, path string) { setMode(t, path, 0o640) }},
	{"WorldReadable", func(t *testing.T, _, path string) { setMode(t, path, 0o644) }},
	{"GroupWritable", func(t *testing.T, _, path string) { setMode(t, path, 0o620) }},
	{"OtherOnly", func(t *testing.T, _, path string) { setMode(t, path, 0o601) }},
	{"ForeignOwner", func(t *testing.T, _, path string) {
		if os.Geteuid() != 0 {
			t.Skip("changing the owner needs root")
		}
		if err := os.Chown(path, os.Geteuid()+4242, -1); err != nil {
			t.Skipf("cannot change the owner: %v", err)
		}
	}},
	{"OverSixteenKiB", func(t *testing.T, _, path string) {
		b := append(readFile(t, path), bytes.Repeat([]byte(" "), 16*1024)...)
		if err := os.WriteFile(path, b, 0o600); err != nil {
			t.Fatal(err)
		}
	}},
	{"SymlinkToValidFile", func(t *testing.T, dir, path string) {
		linked := filepath.Join(dir, "real.json")
		if err := os.Rename(path, linked); err != nil {
			t.Fatal(err)
		}
		if err := os.Symlink(linked, path); err != nil {
			t.Fatal(err)
		}
	}},
	{"DirectoryInsteadOfFile", func(t *testing.T, _, path string) {
		if err := os.Remove(path); err != nil {
			t.Fatal(err)
		}
		if err := os.Mkdir(path, 0o700); err != nil {
			t.Fatal(err)
		}
	}},
	{"FIFOInsteadOfFile", func(t *testing.T, _, path string) {
		if err := os.Remove(path); err != nil {
			t.Fatal(err)
		}
		if err := syscall.Mkfifo(path, 0o600); err != nil {
			t.Skipf("cannot create a FIFO: %v", err)
		}
	}},
}

// openWithin calls Open and fails when it does not return within five seconds.
func openWithin(t *testing.T, dir string, log *slog.Logger) (*Store, Status) {
	t.Helper()
	type result struct {
		s  *Store
		st Status
	}
	ch := make(chan result, 1)
	go func() {
		s, st := Open(dir, log)
		ch <- result{s, st}
	}()
	select {
	case r := <-ch:
		return r.s, r.st
	case <-time.After(5 * time.Second):
		t.Fatal("Open blocked, want a non-blocking open")
		return nil, StatusUnavailable
	}
}

// requireMissing fails unless Open treated the state file as missing: one warning, no path, the zero state.
func requireMissing(t *testing.T, dir string, lc *logCapture, s *Store, st Status) {
	t.Helper()
	if st != StatusUnavailable {
		t.Errorf("status = %v, want StatusUnavailable", st)
	}
	if n := lc.count(t, warnUnavailable); n != 1 {
		t.Errorf("%q logged %d times, want 1", warnUnavailable, n)
	}
	if s == nil {
		t.Fatal("Open returned a nil store")
	}
	if got := s.State(); got.Boot != nil || got.Journal != nil || got.Backup != nil {
		t.Errorf("State() = %+v, want the zero state for a file treated as missing", got)
	}
	for _, l := range lc.lines(t) {
		if strings.Contains(l.Err, dir) || strings.Contains(l.Msg, dir) {
			t.Errorf("log %+v names the path, want no path", l)
		}
	}
}

func TestOpenCorruptOrForeignFileIsMissing(t *testing.T) {
	for _, c := range corruptCases {
		t.Run(c.name, func(t *testing.T) {
			dir, path := savedStore(t)
			c.setup(t, dir, path)

			lc := &logCapture{}
			s, st := openWithin(t, dir, lc.logger())
			requireMissing(t, dir, lc, s, st)
		})
	}
}

func TestOpenSymlinkTargetIsNotRead(t *testing.T) {
	dir, path := savedStore(t)
	linked := filepath.Join(dir, "real.json")
	if err := os.Rename(path, linked); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(linked, path); err != nil {
		t.Fatal(err)
	}
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	if got := s.State(); got.Journal != nil {
		t.Errorf("State() = %+v, want nothing taken from a file behind a symlink", got)
	}
	before := readFile(t, linked)
	update(t, s, true, fullState(99))
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	if after := readFile(t, linked); !bytes.Equal(before, after) {
		t.Error("the save wrote through the symlink, want the target left alone")
	}
	fi, err := os.Lstat(path)
	if err != nil {
		t.Fatal(err)
	}
	if fi.Mode()&os.ModeSymlink != 0 {
		t.Error("the state file is still a symlink after the save, want it replaced by a regular file")
	}
}

func TestOpenCorruptFileIsReplacedBySave(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, FileName)
	if err := os.WriteFile(path, []byte("garbage"), 0o644); err != nil {
		t.Fatal(err)
	}
	s, st := Open(dir, slog.New(slog.DiscardHandler))
	if st != StatusUnavailable {
		t.Fatalf("status = %v, want StatusUnavailable", st)
	}
	update(t, s, true, fullState(5))
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	_, st = Open(dir, slog.New(slog.DiscardHandler))
	if st != StatusLoaded {
		t.Errorf("status after the save = %v, want StatusLoaded", st)
	}
	fi, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if fi.Mode().Perm() != 0o600 {
		t.Errorf("mode = %v, want 0600 for the replaced file", fi.Mode().Perm())
	}
}

func TestSaveFileIsOwnerOnlyEvenWithOpenUmask(t *testing.T) {
	old := syscall.Umask(0)
	defer syscall.Umask(old)
	dir, path := savedStore(t)
	fi, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if fi.Mode().Perm() != 0o600 {
		t.Errorf("mode = %v, want 0600", fi.Mode().Perm())
	}
	if names := dirNames(t, dir); len(names) != 1 || names[0] != FileName {
		t.Errorf("directory holds %v, want only %s", names, FileName)
	}
}

func TestSaveFileIsOwnedByTheAgentAndHoldsJSON(t *testing.T) {
	_, path := savedStore(t)
	var obj map[string]any
	if err := json.Unmarshal(readFile(t, path), &obj); err != nil {
		t.Fatalf("the state file is not JSON: %v", err)
	}
	if v, ok := obj["version"].(float64); !ok || v != 1 {
		t.Errorf("version = %v, want 1", obj["version"])
	}
	fi, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if st, ok := fi.Sys().(*syscall.Stat_t); !ok || int(st.Uid) != os.Geteuid() {
		t.Errorf("owner = %v, want the effective UID %d", fi.Sys(), os.Geteuid())
	}
	if fi.Size() > 16*1024 {
		t.Errorf("size = %d, want at most 16 KiB", fi.Size())
	}
}

func TestSaveSecondSaveReplacesTheFileByRename(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	sv := NewSaver(s, procFS("1000.00"))
	path := filepath.Join(dir, FileName)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	first, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	update(t, s, true, fullState(2))
	collect(t, sv, t0.Add(10*time.Second))
	second, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if os.SameFile(first, second) {
		t.Error("the second save wrote the file in place, want a new file renamed over it")
	}
	if names := dirNames(t, dir); len(names) != 1 || names[0] != FileName {
		t.Errorf("directory holds %v, want only %s", names, FileName)
	}
	s2, st := Open(dir, slog.New(slog.DiscardHandler))
	if st != StatusLoaded || s2.State().Journal == nil || s2.State().Journal.Seqnum != 2 {
		t.Errorf("after the second save: status %v, state %+v, want the newer state", st, s2.State())
	}
}

func TestSaveLeftOverTemporaryFileIsRemoved(t *testing.T) {
	dir := t.TempDir()
	tmp := filepath.Join(dir, FileName+".tmp")
	if err := os.WriteFile(tmp, []byte("stale"), 0o644); err != nil {
		t.Fatal(err)
	}
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, fullState(1))
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	if names := dirNames(t, dir); len(names) != 1 || names[0] != FileName {
		t.Errorf("directory holds %v, want only %s", names, FileName)
	}
	if _, st := Open(dir, slog.New(slog.DiscardHandler)); st != StatusLoaded {
		t.Errorf("status = %v, want StatusLoaded", st)
	}
}

func TestSaveTemporarySymlinkIsNotFollowed(t *testing.T) {
	dir := t.TempDir()
	victim := filepath.Join(t.TempDir(), "victim")
	if err := os.WriteFile(victim, []byte("precious"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(victim, filepath.Join(dir, FileName+".tmp")); err != nil {
		t.Fatal(err)
	}
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, fullState(1))
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	if got := string(readFile(t, victim)); got != "precious" {
		t.Errorf("the symlink target holds %q, want it untouched", got)
	}
	if _, st := Open(dir, slog.New(slog.DiscardHandler)); st != StatusLoaded {
		t.Errorf("status = %v, want StatusLoaded", st)
	}
}

func TestSaveReadOnlyDirectoryLogsNotSavedOnceWithoutPath(t *testing.T) {
	if os.Geteuid() == 0 {
		t.Skip("root writes into a read-only directory")
	}
	dir := t.TempDir()
	lc := &logCapture{}
	s, _ := Open(dir, lc.logger())
	if err := os.Chmod(dir, 0o500); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(dir, 0o700) })
	update(t, s, true, fullState(1))
	sv := NewSaver(s, procFS("1000.00"))
	collect(t, sv, t0)
	collect(t, sv, t0.Add(20*time.Second))
	if n := lc.count(t, warnNotSaved); n != 1 {
		t.Errorf("%q logged %d times, want 1", warnNotSaved, n)
	}
}

func TestSaverMissingDirectoryLogsNotSavedOnce(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "spool")
	lc := &logCapture{}
	s, _ := Open(dir, lc.logger())
	sv := NewSaver(s, procFS("1000.00"))
	update(t, s, true, fullState(1))
	for i := 0; i < 3; i++ {
		_, _ = sv.Collect(context.Background(), scheduler.Run{Start: t0.Add(time.Duration(i) * 20 * time.Second)})
		update(t, s, true, fullState(uint64(i+2)))
	}
	if n := lc.count(t, warnNotSaved); n != 1 {
		t.Errorf("%q logged %d times in three failing saves, want once until a save succeeds", warnNotSaved, n)
	}
	for _, l := range lc.lines(t) {
		if l.Msg != warnNotSaved {
			continue
		}
		if l.Level != "WARN" {
			t.Errorf("level = %s, want WARN", l.Level)
		}
		if l.Err == "" {
			t.Error("the warning has no err attribute, want the OS error text")
		}
		if strings.Contains(l.Err, dir) || strings.Contains(l.Err, FileName) {
			t.Errorf("err = %q names the path, want no path", l.Err)
		}
	}
	if _, err := os.Stat(dir); err == nil {
		t.Error("the save created the missing directory, want the agent to never create it")
	}
}

func TestSaverMissingDirectoryLogsAgainAfterASuccessfulSave(t *testing.T) {
	root := t.TempDir()
	dir := filepath.Join(root, "spool")
	lc := &logCapture{}
	s, _ := Open(dir, lc.logger())
	sv := NewSaver(s, procFS("1000.00"))
	update(t, s, true, fullState(1))
	_, _ = sv.Collect(context.Background(), scheduler.Run{Start: t0})
	if err := os.Mkdir(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	update(t, s, true, fullState(2))
	collect(t, sv, t0.Add(20*time.Second))
	if _, err := os.Stat(filepath.Join(dir, FileName)); err != nil {
		t.Fatalf("the save after the directory appeared failed: %v", err)
	}
	if err := os.RemoveAll(dir); err != nil {
		t.Fatal(err)
	}
	update(t, s, true, fullState(3))
	_, _ = sv.Collect(context.Background(), scheduler.Run{Start: t0.Add(40 * time.Second)})
	if n := lc.count(t, warnNotSaved); n != 2 {
		t.Errorf("%q logged %d times around one successful save, want 2", warnNotSaved, n)
	}
}

func TestSaverName(t *testing.T) {
	s, _ := Open(t.TempDir(), slog.New(slog.DiscardHandler))
	if got := NewSaver(s, procFS("1.00")).Name(); got != "state" {
		t.Errorf("Name() = %q, want %q", got, "state")
	}
}

// ratesSetup returns a store in a fresh directory, its saver and the path of the state file.
func ratesSetup(t *testing.T) (*Store, *Saver, string) {
	t.Helper()
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	return s, NewSaver(s, procFS("1000.00")), filepath.Join(dir, FileName)
}

func TestSaverWriteNothingChangedWritesNoFile(t *testing.T) {
	_, sv, path := ratesSetup(t)
	collect(t, sv, t0)
	if _, err := os.Stat(path); err == nil {
		t.Error("a state file exists without any change, want none")
	}
}

func TestSaverWriteFirstUrgentChangeIsSavedAtOnce(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	if _, err := os.Stat(path); err != nil {
		t.Errorf("no file after an urgent change: %v", err)
	}
}

func TestSaverWriteUrgentChangeWaitsTenSecondsSinceTheLastSave(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	saved := readFile(t, path)

	update(t, s, true, fullState(2))
	collect(t, sv, t0.Add(9*time.Second))
	if !bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was saved 9 s after the last save, want it to wait for 10 s")
	}
	collect(t, sv, t0.Add(10*time.Second))
	if bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was not saved 10 s after the last save")
	}
}

func TestSaverWritePositionOnlyChangeWaitsForTheFiftySecondHeartbeat(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	saved := readFile(t, path)

	update(t, s, false, func(st *State) { st.Journal.Seqnum = 2 })
	for _, d := range []time.Duration{10 * time.Second, 30 * time.Second, 49 * time.Second} {
		collect(t, sv, t0.Add(d))
		if !bytes.Equal(saved, readFile(t, path)) {
			t.Fatalf("a position-only change was saved %v after the last save, want it to wait for 50 s", d)
		}
	}
	collect(t, sv, t0.Add(50*time.Second))
	if bytes.Equal(saved, readFile(t, path)) {
		t.Error("a position-only change was not saved 50 s after the last save")
	}
	s2, st := Open(filepath.Dir(path), slog.New(slog.DiscardHandler))
	if st != StatusLoaded || s2.State().Journal == nil || s2.State().Journal.Seqnum != 2 {
		t.Errorf("after the heartbeat: status %v, state %+v, want seqnum 2", st, s2.State())
	}
}

func TestSaverWriteUrgentChangeCarriesAWaitingPositionAlong(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	update(t, s, false, func(st *State) { st.Journal.Seqnum = 2 })
	update(t, s, true, func(st *State) { st.Backup.MaxProcesses = 9 })
	collect(t, sv, t0.Add(10*time.Second))
	s2, _ := Open(filepath.Dir(path), slog.New(slog.DiscardHandler))
	got := s2.State()
	if got.Backup == nil || got.Backup.MaxProcesses != 9 || got.Journal == nil || got.Journal.Seqnum != 2 {
		t.Errorf("State() = %+v, want the urgent change and the newer position saved together", got)
	}
}

func TestSaverWriteUrgentWithinTenSecondsAfterHeartbeatSaveWaits(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	update(t, s, false, func(st *State) { st.Journal.Seqnum = 2 })
	collect(t, sv, t0.Add(50*time.Second))
	saved := readFile(t, path)
	update(t, s, true, func(st *State) { st.Backup.MaxProcesses = 9 })
	collect(t, sv, t0.Add(59*time.Second))
	if !bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was saved 9 s after the last save, want it to wait for 10 s")
	}
	collect(t, sv, t0.Add(60*time.Second))
	if bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was not saved 10 s after the last save")
	}
}

func TestSaverWriteHeartbeatSavesTheUptimeFromProcEveryFiftySeconds(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	path := filepath.Join(dir, FileName)
	update(t, s, true, fullState(1))
	collect(t, NewSaver(s, procFS("1000.00")), t0)

	sv := NewSaver(s, procFS("1049.50"))
	collect(t, sv, t0.Add(49*time.Second))
	s2, _ := Open(dir, slog.New(slog.DiscardHandler))
	if got := s2.State().Boot; got == nil || got.Uptime != 1000*time.Second {
		t.Fatalf("Boot after 49 s = %+v, want the uptime of the first save", got)
	}
	collect(t, sv, t0.Add(50*time.Second))
	s3, st := Open(dir, slog.New(slog.DiscardHandler))
	got := s3.State().Boot
	if st != StatusLoaded || got == nil {
		t.Fatalf("status %v, Boot %+v after the heartbeat, want a loaded boot", st, got)
	}
	if got.Uptime != 1049*time.Second+500*time.Millisecond {
		t.Errorf("Uptime = %v, want 1049.5s from /proc/uptime", got.Uptime)
	}
	if got.ID != bootID {
		t.Errorf("Boot.ID = %q, want %q kept", got.ID, bootID)
	}
	if fi, err := os.Stat(path); err != nil || fi.Mode().Perm() != 0o600 {
		t.Errorf("state file stat = %v, %v, want mode 0600", fi, err)
	}
}

func TestSaverWriteUnreadableUptimeStillSavesTheChange(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, fullState(1))
	collect(t, NewSaver(s, fstest.MapFS{}), t0)
	if _, st := Open(dir, slog.New(slog.DiscardHandler)); st != StatusLoaded {
		t.Errorf("status = %v, want StatusLoaded without /proc/uptime", st)
	}
}

func TestStoreUpdateChangesStateInPlace(t *testing.T) {
	s, _ := Open(t.TempDir(), slog.New(slog.DiscardHandler))
	update(t, s, false, fullState(1))
	update(t, s, false, func(st *State) {
		if st.Journal == nil || st.Journal.Seqnum != 1 {
			t.Errorf("fn saw %+v, want the state of the earlier update", st.Journal)
			return
		}
		st.Journal.Seqnum = 2
	})
	if got := s.State(); got.Journal == nil || got.Journal.Seqnum != 2 {
		t.Errorf("State().Journal = %+v, want seqnum 2", got.Journal)
	}
}

func TestStoreStateReturnsACopy(t *testing.T) {
	s, _ := Open(t.TempDir(), slog.New(slog.DiscardHandler))
	update(t, s, false, fullState(1))
	got := s.State()
	if got.Journal == nil || got.Boot == nil || got.Backup == nil {
		t.Fatalf("State() = %+v, want the updated state", got)
	}
	got.Journal.Seqnum = 99
	got.Boot.ID = "changed"
	got.Backup.MaxProcesses = 99
	again := s.State()
	if again.Journal.Seqnum != 1 || again.Boot.ID != bootID || again.Backup.MaxProcesses != 3 {
		t.Errorf("State() = %+v after changing a returned copy, want the store untouched", again)
	}
}

func TestStoreConcurrentUpdateAndCollect(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	sv := NewSaver(s, procFS("1000.00"))
	update(t, s, true, fullState(0))
	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		for i := uint64(1); i <= 50; i++ {
			update(t, s, i%5 == 0, func(st *State) { st.Journal.Seqnum = i })
			_ = s.State()
		}
	}()
	go func() {
		defer wg.Done()
		for i := 0; i < 50; i++ {
			_, _ = sv.Collect(context.Background(), scheduler.Run{Start: t0.Add(time.Duration(i) * 11 * time.Second)})
		}
	}()
	wg.Wait()
	if _, st := Open(dir, slog.New(slog.DiscardHandler)); st != StatusLoaded {
		t.Errorf("status = %v, want StatusLoaded after concurrent saves", st)
	}
}

// setUptime sets the first field of proc/uptime to seconds.
func setUptime(proc fstest.MapFS, seconds int) {
	proc["uptime"] = &fstest.MapFile{Data: []byte(fmt.Sprintf("%d.00 4000.00\n", seconds))}
}

// savedBoot reopens the state in dir and returns its boot.
func savedBoot(t *testing.T, dir string) *Boot {
	t.Helper()
	s, st := Open(dir, slog.New(slog.DiscardHandler))
	if st != StatusLoaded {
		t.Fatalf("status = %v, want StatusLoaded", st)
	}
	return s.State().Boot
}

func TestSaverUrgentSaveRefreshesTheUptime(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	proc := procFS("1000.00")
	sv := NewSaver(s, proc)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	for _, d := range []int{10, 20, 30} {
		setUptime(proc, 1000+d)
		collect(t, sv, t0.Add(time.Duration(d)*time.Second))
	}
	setUptime(proc, 1040)
	update(t, s, true, func(st *State) { st.Backup.MaxProcesses = 9 })
	collect(t, sv, t0.Add(40*time.Second))
	got := savedBoot(t, dir)
	if got == nil || got.Uptime != 1040*time.Second {
		t.Errorf("Boot = %+v after an urgent save at 40 s, want the uptime of that moment, 1040s", got)
	}
}

func TestSaverUrgentStormKeepsTheUptimeWithinAMinute(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	proc := procFS("1000.00")
	sv := NewSaver(s, proc)
	update(t, s, true, fullState(0))
	collect(t, sv, t0)
	for d := uint64(30); d <= 600; d += 30 {
		setUptime(proc, int(1000+d))
		update(t, s, true, func(st *State) { st.Journal.Seqnum = d })
		collect(t, sv, t0.Add(time.Duration(d)*time.Second))
		got := savedBoot(t, dir)
		want := time.Duration(1000+d) * time.Second
		if got == nil || got.Uptime != want {
			t.Fatalf("Boot = %+v after the urgent save at %d s, want uptime %v", got, d, want)
		}
	}
}

func TestSaverWithoutBootWritesNoUptime(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, func(st *State) {
		st.Journal = &JournalPosition{SeqnumID: seqnumID, Seqnum: 1, Realtime: t0}
	})
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	if got := savedBoot(t, dir); got != nil {
		t.Errorf("Boot = %+v, want none when no boot is set", got)
	}
}

// setField sets obj[section][key] to value.
func setField(t *testing.T, obj map[string]any, section, key string, value any) {
	t.Helper()
	m, ok := obj[section].(map[string]any)
	if !ok {
		t.Fatalf("the state file holds no %q object", section)
	}
	m[key] = value
}

func TestOpenInvalidOpenRunIsMissing(t *testing.T) {
	const (
		zeroTime  = "0001-01-01T00:00:00Z"
		year10000 = "10000-01-01T00:00:00Z"
	)
	started := t0.Add(-time.Hour).Format(time.RFC3339)
	cases := []struct {
		name            string
		section, key    string
		value           any
		wantLoadedState bool
	}{
		{name: "LastSeenBeforeStartedAt", section: "backup", key: "last_seen", value: t0.Add(-2 * time.Hour).Format(time.RFC3339)},
		{name: "MaxProcessesZero", section: "backup", key: "max_processes", value: 0},
		{name: "StartedAtZero", section: "backup", key: "started_at", value: zeroTime},
		{name: "LastSeenZero", section: "backup", key: "last_seen", value: zeroTime},
		{name: "StartedAtInYear10000", section: "backup", key: "started_at", value: year10000},
		{name: "LastSeenInYear10000", section: "backup", key: "last_seen", value: year10000},
		{name: "JournalRealtimeInYear10000", section: "journal", key: "realtime", value: year10000},
		{name: "BootedAtInYear10000", section: "boot", key: "booted_at", value: year10000},
		{name: "ValidOpenRunLoads", section: "backup", key: "started_at", value: started, wantLoadedState: true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			dir, path := savedStore(t)
			mutateJSON(t, path, func(obj map[string]any) { setField(t, obj, c.section, c.key, c.value) })
			lc := &logCapture{}
			s, st := openWithin(t, dir, lc.logger())
			if c.wantLoadedState {
				if st != StatusLoaded || s.State().Backup == nil {
					t.Errorf("status = %v, state %+v, want a valid open run loaded", st, s.State())
				}
				if n := lc.count(t, warnUnavailable); n != 0 {
					t.Errorf("%q logged %d times, want none", warnUnavailable, n)
				}
				return
			}
			requireMissing(t, dir, lc, s, st)
		})
	}
}
