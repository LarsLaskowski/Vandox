package agentstate

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"math"
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

// contains reports whether the captured log holds text.
func (c *logCapture) contains(text string) bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return bytes.Contains(c.buf.Bytes(), []byte(text))
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
	{"OverSixtyFourKiB", func(t *testing.T, _, path string) {
		b := append(readFile(t, path), bytes.Repeat([]byte(" "), 64*1024)...)
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

func TestSaverWriteUrgentChangeWaitsFiveSecondsSinceTheLastSave(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	saved := readFile(t, path)

	update(t, s, true, fullState(2))
	collect(t, sv, t0.Add(4*time.Second))
	if !bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was saved 4 s after the last save, want it to wait for 5 s")
	}
	collect(t, sv, t0.Add(5*time.Second))
	if bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was not saved 5 s after the last save")
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

func TestSaverWriteUrgentWithinFiveSecondsAfterHeartbeatSaveWaits(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0)
	update(t, s, false, func(st *State) { st.Journal.Seqnum = 2 })
	collect(t, sv, t0.Add(50*time.Second))
	saved := readFile(t, path)
	update(t, s, true, func(st *State) { st.Backup.MaxProcesses = 9 })
	collect(t, sv, t0.Add(54*time.Second))
	if !bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was saved 4 s after the last save, want it to wait for 5 s")
	}
	collect(t, sv, t0.Add(55*time.Second))
	if bytes.Equal(saved, readFile(t, path)) {
		t.Error("an urgent change was not saved 5 s after the last save")
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

// digest returns the 64 lower-case hex digits of the SHA-256 of s.
func digest(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])
}

// sampleLogs returns log positions with values that survive a round trip through a decoded JSON number (below 2^53).
func sampleLogs() *LogPositions {
	return &LogPositions{
		Journal: []JournalPosition{
			{SeqnumID: seqnumID, Seqnum: 1234, Realtime: t0.Add(-time.Minute)},
			{SeqnumID: "2f8fad5b-d9cb-469f-a165-70867728950e", Seqnum: 99, Realtime: t0.Add(-time.Hour)},
		},
		Files: []FilePosition{
			{PathSHA256: digest("/var/log/mail.log"), Device: 2049, Inode: 77, Offset: 4096, HeadSHA256: digest("head"), HeadLen: 256, LastTime: t0.Add(-time.Second),
				Renamed: &RenamedFile{Device: 2049, Inode: 78, Offset: 100, Size: 200}},
			{PathSHA256: digest("/var/log/other.log"), Device: 2049, Inode: 79, Offset: 0, HeadSHA256: digest(""), HeadLen: 0, LastTime: t0},
		},
	}
}

// worstCaseLogs returns the largest log positions the state file holds: 256 journal positions and 16 file positions, each with a
// renamed file, with the longest numbers and times.
func worstCaseLogs() *LogPositions {
	latest := time.Date(9999, time.December, 31, 23, 59, 59, 999999999, time.UTC)
	logs := &LogPositions{}
	for i := 0; i < MaxLogJournals; i++ {
		logs.Journal = append(logs.Journal, JournalPosition{
			SeqnumID: fmt.Sprintf("%08x-d9cb-469f-a165-%012x", i, i), Seqnum: math.MaxUint64, Realtime: latest,
		})
	}
	for i := 0; i < MaxLogFiles; i++ {
		logs.Files = append(logs.Files, FilePosition{
			PathSHA256: digest(fmt.Sprintf("/var/log/file%d.log", i)), Device: math.MaxUint64, Inode: math.MaxUint64, Offset: math.MaxInt64,
			HeadSHA256: digest(fmt.Sprintf("head%d", i)), HeadLen: 256, LastTime: latest,
			Renamed: &RenamedFile{Device: math.MaxUint64, Inode: math.MaxUint64, Offset: math.MaxInt64, Size: math.MaxInt64},
		})
	}
	return logs
}

// equalLogs reports whether two sets of log positions hold the same values.
func equalLogs(a, b *LogPositions) bool {
	if a == nil || b == nil {
		return a == b
	}
	if len(a.Journal) != len(b.Journal) || len(a.Files) != len(b.Files) {
		return false
	}
	for i := range a.Journal {
		x, y := a.Journal[i], b.Journal[i]
		if x.SeqnumID != y.SeqnumID || x.Seqnum != y.Seqnum || !x.Realtime.Equal(y.Realtime) {
			return false
		}
	}
	for i := range a.Files {
		x, y := a.Files[i], b.Files[i]
		if x.PathSHA256 != y.PathSHA256 || x.Device != y.Device || x.Inode != y.Inode || x.Offset != y.Offset || x.HeadSHA256 != y.HeadSHA256 ||
			x.HeadLen != y.HeadLen || !x.LastTime.Equal(y.LastTime) || (x.Renamed == nil) != (y.Renamed == nil) {
			return false
		}
		if x.Renamed != nil && *x.Renamed != *y.Renamed {
			return false
		}
	}
	return true
}

func TestSaver_SavesALogPositionWithinTenSeconds(t *testing.T) {
	s, sv, path := ratesSetup(t)
	update(t, s, true, fullState(1))
	collect(t, sv, t0) // the last save
	update(t, s, true, func(st *State) { st.Logs = sampleLogs() })

	// the run starts a few milliseconds early on the 10 s grid of the state job (the threshold is 5 s, so jitter is tolerated)
	collect(t, sv, t0.Add(10*time.Second-5*time.Millisecond))

	s2, status := Open(filepath.Dir(path), slog.New(slog.DiscardHandler))
	if status != StatusLoaded {
		t.Fatalf("status = %v, want StatusLoaded", status)
	}
	if got := s2.State().Logs; !equalLogs(got, sampleLogs()) {
		t.Errorf("saved log positions = %+v, want %+v: a committed position is saved within one state interval", got, sampleLogs())
	}
}

func TestEncode_FullLogPositionsFitTheBound(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, func(st *State) {
		fullState(1)(st)
		st.Logs = worstCaseLogs()
	})

	collect(t, NewSaver(s, procFS("1000.00")), t0)

	fi, err := os.Stat(filepath.Join(dir, FileName))
	if err != nil {
		t.Fatalf("the state file is missing: %v", err)
	}
	if fi.Size() > 64*1024 || fi.Size() <= 16*1024 {
		t.Errorf("the state file holds %d bytes, want more than 16 KiB (the old bound) and at most 64 KiB for 256 journal and 16 file positions", fi.Size())
	}
	s2, status := Open(dir, slog.New(slog.DiscardHandler))
	if status != StatusLoaded {
		t.Fatalf("status = %v, want StatusLoaded for a file of %d bytes", status, fi.Size())
	}
	if got := s2.State().Logs; !equalLogs(got, worstCaseLogs()) {
		n := 0
		if got != nil {
			n = len(got.Journal)
		}
		t.Errorf("read back %d journal positions, want 256 file and journal positions read back intact", n)
	}
	if got := s2.State(); got.Boot == nil || got.Journal == nil || got.Backup == nil {
		t.Errorf("State() = %+v, want the boot, the kernel position and the backup run saved beside the log positions", got)
	}
}

// savedLogStore returns a directory with a saved state file that holds the sample log positions and the file's path.
func savedLogStore(t *testing.T) (string, string) {
	t.Helper()
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	update(t, s, true, func(st *State) {
		fullState(7)(st)
		st.Logs = sampleLogs()
	})
	collect(t, NewSaver(s, procFS("1000.00")), t0)
	return dir, filepath.Join(dir, FileName)
}

// logsLists returns the saved object of the log positions and its two lists, found by the keys their elements have, whatever the
// lists are called.
func logsLists(t *testing.T, obj map[string]any) (logs map[string]any, journalKey string, journal []any, filesKey string, files []any) {
	t.Helper()
	logs, ok := obj["logs"].(map[string]any)
	if !ok {
		t.Fatalf("the state file holds no object of log positions: %v", obj)
	}
	for key, v := range logs {
		list, ok := v.([]any)
		if !ok || len(list) == 0 {
			continue
		}
		first, _ := list[0].(map[string]any)
		if _, has := first["seqnum_id"]; has {
			journalKey, journal = key, list
		}
		if _, has := first["path_sha256"]; has {
			filesKey, files = key, list
		}
	}
	if journalKey == "" || filesKey == "" {
		t.Fatalf("the log positions %v hold no list of journal positions or no list of file positions", logs)
	}
	return logs, journalKey, journal, filesKey, files
}

// logMutation changes the decoded state file.
type logMutation func(t *testing.T, obj map[string]any)

// firstJournal, firstFile and firstRenamed return the first journal position, the first file position and the renamed file of the first
// file position of the saved state, as the objects to change.
func firstJournal(t *testing.T, obj map[string]any) map[string]any {
	t.Helper()
	_, _, journal, _, _ := logsLists(t, obj)
	first, _ := journal[0].(map[string]any)
	return first
}

func firstFile(t *testing.T, obj map[string]any) map[string]any {
	t.Helper()
	_, _, _, _, files := logsLists(t, obj)
	first, _ := files[0].(map[string]any)
	return first
}

func firstRenamed(t *testing.T, obj map[string]any) map[string]any {
	t.Helper()
	renamed, ok := firstFile(t, obj)["renamed"].(map[string]any)
	if !ok {
		t.Fatal("the first file position holds no renamed file")
	}
	return renamed
}

func setJournal(key string, value any) logMutation {
	return func(t *testing.T, obj map[string]any) { firstJournal(t, obj)[key] = value }
}

func setFile(key string, value any) logMutation {
	return func(t *testing.T, obj map[string]any) { firstFile(t, obj)[key] = value }
}

func setRenamed(key string, value any) logMutation {
	return func(t *testing.T, obj map[string]any) { firstRenamed(t, obj)[key] = value }
}

func setLogs(key string, value any) logMutation {
	return func(t *testing.T, obj map[string]any) {
		logs, _, _, _, _ := logsLists(t, obj)
		logs[key] = value
	}
}

// grow adds n copies of the first journal positions (with other IDs) or file positions (with other path hashes).
func grow(journal bool, n int) logMutation {
	return func(t *testing.T, obj map[string]any) {
		t.Helper()
		logs, journalKey, _, filesKey, _ := logsLists(t, obj)
		for i := 0; i < n; i++ {
			if journal {
				el := copyObject(firstJournal(t, obj))
				el["seqnum_id"] = fmt.Sprintf("%08x-d9cb-469f-a165-70867728950e", i+100)
				logs[journalKey] = append(logs[journalKey].([]any), el)
			} else {
				el := copyObject(firstFile(t, obj))
				el["path_sha256"] = digest(fmt.Sprintf("/var/log/extra%d.log", i))
				logs[filesKey] = append(logs[filesKey].([]any), el)
			}
		}
	}
}

func copyObject(src map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range src {
		out[k] = v
	}
	return out
}

// invalidLogCases are changes of the saved log positions that make the state file invalid.
func invalidLogCases() []struct {
	name   string
	mutate logMutation
} {
	upper := strings.ToUpper(digest("x"))
	return []struct {
		name   string
		mutate logMutation
	}{
		{"UnknownKeyInLogs", setLogs("unexpected", 1)},
		{"UnknownKeyInAJournalPosition", setJournal("unexpected", 1)},
		{"UnknownKeyInAFilePosition", setFile("unexpected", 1)},
		{"UnknownKeyInTheRenamedFile", setRenamed("unexpected", 1)},
		{"LogsNotAnObject", func(_ *testing.T, obj map[string]any) { obj["logs"] = []any{} }},
		{"JournalSeqnumIDNotAUUID", setJournal("seqnum_id", "not-a-uuid")},
		{"JournalSeqnumIDUpperCase", setJournal("seqnum_id", strings.ToUpper(seqnumID))},
		{"JournalSeqnumAsString", setJournal("seqnum", "7")},
		{"JournalRealtimeNotATime", setJournal("realtime", "yesterday")},
		{"JournalRealtimeInYear10000", setJournal("realtime", "10000-01-01T00:00:00Z")},
		{"PathHashTooShort", setFile("path_sha256", digest("x")[:63])},
		{"PathHashUpperCase", setFile("path_sha256", upper)},
		{"PathHashNotHex", setFile("path_sha256", strings.Repeat("g", 64))},
		{"HeadHashUpperCase", setFile("head_sha256", upper)},
		{"HeadLengthAbove256", setFile("head_len", 257)},
		{"HeadLengthNegative", setFile("head_len", -1)},
		{"OffsetNegative", setFile("offset", -1)},
		{"DeviceNegative", setFile("device", -1)},
		{"LastTimeNotATime", setFile("last_time", "yesterday")},
		{"RenamedOffsetNegative", setRenamed("offset", -1)},
		{"RenamedSizeNegative", setRenamed("size", -1)},
		{"MoreThan256JournalPositions", grow(true, MaxLogJournals-1)},
		{"MoreThan16FilePositions", grow(false, MaxLogFiles-1)},
	}
}

func TestDecode_RefusesInvalidLogPositions(t *testing.T) {
	for _, c := range invalidLogCases() {
		t.Run(c.name, func(t *testing.T) {
			dir, path := savedLogStore(t)
			mutateJSON(t, path, func(obj map[string]any) { c.mutate(t, obj) })

			lc := &logCapture{}
			s, st := openWithin(t, dir, lc.logger())

			requireMissing(t, dir, lc, s, st)
			if got := s.State().Logs; got != nil {
				t.Errorf("State().Logs = %+v, want none from a file treated as missing", got)
			}
		})
	}
}

func TestDecode_ReadsUnchangedAndMaximalLogPositions(t *testing.T) {
	t.Run("the unchanged file", func(t *testing.T) {
		dir, path := savedLogStore(t)
		mutateJSON(t, path, func(map[string]any) {})

		s, st := openWithin(t, dir, slog.New(slog.DiscardHandler))

		if st != StatusLoaded || !equalLogs(s.State().Logs, sampleLogs()) {
			t.Errorf("Open() status = %v, logs = %+v, want StatusLoaded and the saved log positions", st, s.State().Logs)
		}
	})
	t.Run("the limits themselves", func(t *testing.T) {
		dir, path := savedLogStore(t)
		mutateJSON(t, path, func(obj map[string]any) {
			grow(true, MaxLogJournals-2)(t, obj)
			grow(false, MaxLogFiles-2)(t, obj)
			setFile("head_len", 256)(t, obj)
		})

		s, st := openWithin(t, dir, slog.New(slog.DiscardHandler))

		if logs := s.State().Logs; st != StatusLoaded || logs == nil || len(logs.Journal) != MaxLogJournals || len(logs.Files) != MaxLogFiles {
			t.Errorf("Open() status = %v, logs = %+v, want StatusLoaded with %d journal and %d file positions", st, logs, MaxLogJournals, MaxLogFiles)
		}
	})
}

func TestStore_FlushSavesAChangedStateOnly(t *testing.T) {
	dir := t.TempDir()
	s, _ := Open(dir, slog.New(slog.DiscardHandler))
	path := filepath.Join(dir, FileName)
	update(t, s, false, fullState(3))

	if err := s.Flush(t0); err != nil {
		t.Fatalf("Flush() error = %v, want nil", err)
	}

	s2, status := Open(dir, slog.New(slog.DiscardHandler))
	if status != StatusLoaded || s2.State().Journal == nil || s2.State().Journal.Seqnum != 3 {
		t.Fatalf("after Flush: status %v, state %+v, want the saved position 3", status, s2.State())
	}
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err := s.Flush(t0.Add(time.Second)); err != nil {
		t.Fatalf("Flush() of an unchanged state error = %v, want nil", err)
	}
	if _, err := os.Stat(path); err == nil {
		t.Error("Flush wrote the state file again although nothing changed since the last save")
	}
	update(t, s, false, func(st *State) { st.Journal.Seqnum = 4 })
	if err := s.Flush(t0.Add(2 * time.Second)); err != nil {
		t.Fatalf("Flush() of a changed state error = %v, want nil", err)
	}
	if s3, _ := Open(dir, slog.New(slog.DiscardHandler)); s3.State().Journal == nil || s3.State().Journal.Seqnum != 4 {
		t.Errorf("after the second Flush: state %+v, want the position 4", s3.State())
	}
}

func TestStore_FlushWaitsForARunningSave(t *testing.T) {
	const rounds = 100
	dir := t.TempDir()
	lc := &logCapture{}
	s, _ := Open(dir, lc.logger())
	update(t, s, true, fullState(0))
	errs := make(chan error, rounds)
	var wg sync.WaitGroup
	wg.Add(2)

	go saveRepeatedly(&wg, NewSaver(s, procFS("1000.00")), rounds)
	go flushRepeatedly(t, &wg, s, rounds, errs)
	wg.Wait()
	close(errs)

	for err := range errs {
		t.Errorf("Flush() error = %v, want nil: it waits for a running save and then saves", err)
	}
	if n := lc.count(t, warnNotSaved); n != 0 {
		t.Errorf("%q was logged %d times, want 0: the temporary file of one save must not be in the way of the other", warnNotSaved, n)
	}
	if err := s.Flush(t0.Add(time.Hour)); err != nil {
		t.Fatalf("Flush() error = %v, want nil", err)
	}
	if s2, status := Open(dir, slog.New(slog.DiscardHandler)); status != StatusLoaded || s2.State().Journal == nil || s2.State().Journal.Seqnum != rounds {
		t.Errorf("after the last Flush: status %v, state %+v, want the position %d", status, s2.State(), rounds)
	}
}

// saveRepeatedly runs the state job rounds times, each time at a start 11 s after the one before.
func saveRepeatedly(wg *sync.WaitGroup, sv *Saver, rounds int) {
	defer wg.Done()
	for i := 0; i < rounds; i++ {
		_, _ = sv.Collect(context.Background(), scheduler.Run{Start: t0.Add(time.Duration(i) * 11 * time.Second)})
	}
}

// flushRepeatedly changes the position rounds times and flushes the store after each change; the errors go to errs.
func flushRepeatedly(t *testing.T, wg *sync.WaitGroup, s *Store, rounds int, errs chan<- error) {
	defer wg.Done()
	for i := 1; i <= rounds; i++ {
		seq := uint64(i) // #nosec G115 -- a small positive counter
		update(t, s, true, func(st *State) { st.Journal.Seqnum = seq })
		if err := s.Flush(t0.Add(time.Duration(i) * 11 * time.Second)); err != nil {
			errs <- err
		}
	}
}

func TestStore_FlushGivesUpWhenASaveDoesNotEnd(t *testing.T) {
	dir := t.TempDir()
	lc := &logCapture{}
	s, _ := Open(dir, lc.logger())
	path := filepath.Join(dir, FileName)
	update(t, s, false, fullState(1))
	if err := s.Flush(t0); err != nil {
		t.Fatalf("Flush() of the first state error = %v, want nil", err)
	}
	before := readFile(t, path)
	update(t, s, false, func(st *State) { st.Journal.Seqnum = 2 })

	fired := make(chan time.Time)
	close(fired)
	var waited []time.Duration
	original := flushAfter
	flushAfter = func(d time.Duration) <-chan time.Time {
		waited = append(waited, d)
		return fired
	}
	t.Cleanup(func() { flushAfter = original })
	s.saving <- struct{}{}

	err := s.Flush(t0.Add(time.Second))

	if err == nil || err.Error() != "agentstate: save still running" {
		t.Errorf("Flush() error = %v, want %q", err, "agentstate: save still running")
	}
	if len(waited) != 1 || waited[0] != 5*time.Second {
		t.Errorf("Flush() waited for %v, want one wait of 5s", waited)
	}
	var warnings []logLine
	for _, l := range lc.lines(t) {
		if l.Msg == warnNotSaved {
			warnings = append(warnings, l)
		}
	}
	if len(warnings) != 1 || warnings[0].Level != "WARN" || warnings[0].Err != "agentstate: save still running" {
		t.Errorf("warnings %q = %+v, want exactly one WARN with err %q", warnNotSaved, warnings, "agentstate: save still running")
	}
	if after := readFile(t, path); !bytes.Equal(after, before) {
		t.Errorf("the state file = %s, want it unchanged (%s) by a Flush that gave up", after, before)
	}

	<-s.saving
	flushAfter = original
	if err := s.Flush(t0.Add(2 * time.Second)); err != nil {
		t.Fatalf("Flush() after the running save ended error = %v, want nil", err)
	}
	if s2, _ := Open(dir, slog.New(slog.DiscardHandler)); s2.State().Journal == nil || s2.State().Journal.Seqnum != 2 {
		t.Errorf("state after the later Flush = %+v, want the position 2 that the first Flush kept dirty", s2.State())
	}
}

func TestDecode_StateWithoutLogPositionsStaysUsable(t *testing.T) {
	dir, path := savedStore(t)
	mutateJSON(t, path, func(obj map[string]any) {
		if _, has := obj["logs"]; has {
			t.Fatalf("the state file %v already holds log positions, want a file of the earlier format", obj)
		}
	})

	s, status := openWithin(t, dir, slog.New(slog.DiscardHandler))

	if status != StatusLoaded {
		t.Fatalf("Open() status = %v, want StatusLoaded for a file without log positions", status)
	}
	got := s.State()
	if got.Logs != nil {
		t.Errorf("State().Logs = %+v, want none", got.Logs)
	}
	if got.Boot == nil || got.Journal == nil || got.Backup == nil {
		t.Errorf("State() = %+v, want the boot, the kernel position and the backup run read as before", got)
	}
	update(t, s, true, func(st *State) { st.Logs = sampleLogs() })
	if err := s.Flush(t0); err != nil {
		t.Fatalf("Flush() error = %v, want nil", err)
	}
	if s2, st := Open(dir, slog.New(slog.DiscardHandler)); st != StatusLoaded || !equalLogs(s2.State().Logs, sampleLogs()) {
		t.Errorf("Open() after adding log positions: status %v, logs %+v, want StatusLoaded and the added positions", st, s2.State().Logs)
	}
}

func TestDecode_RefusesAnInvalidKernelJournalPosition(t *testing.T) {
	cases := []struct {
		name  string
		value string
	}{
		{"NotAUUID", "not-a-uuid"},
		{"UpperCase", strings.ToUpper(seqnumID)},
		{"Empty", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			dir, path := savedStore(t)
			mutateJSON(t, path, func(obj map[string]any) { setField(t, obj, "journal", "seqnum_id", c.value) })
			lc := &logCapture{}

			s, st := openWithin(t, dir, lc.logger())

			requireMissing(t, dir, lc, s, st)
		})
	}
}

const (
	reasonNotDurable = "agentstate: save: spool not durable"
	barrierGuard     = 2 * time.Second
)

// barrierStore returns a store in a fresh directory with a changed state, its saver and the path of the state file.
func barrierStore(t *testing.T, lc *logCapture) (*Store, *Saver, string) {
	t.Helper()
	dir := t.TempDir()
	s, _ := Open(dir, lc.logger())
	update(t, s, true, fullState(1))
	return s, NewSaver(s, procFS("1000.00")), filepath.Join(dir, FileName)
}

// fileExists reports whether path exists.
func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func TestStore_SaveWaitsForBarrier(t *testing.T) {
	s, sv, path := barrierStore(t, &logCapture{})
	entered := make(chan struct{})
	release := make(chan struct{})
	s.SetBarrier(func(context.Context) error {
		close(entered)
		<-release
		return nil
	})
	done := make(chan error, 1)
	go func() {
		_, err := sv.Collect(context.Background(), scheduler.Run{Start: t0})
		done <- err
	}()

	select {
	case <-entered:
	case <-time.After(barrierGuard):
		close(release)
		t.Fatal("the state job did not call the barrier before saving")
	}
	if fileExists(path) {
		t.Error("the state file exists while the barrier is held back, want the save to wait for it")
	}
	select {
	case err := <-done:
		t.Fatalf("Collect returned %v while the barrier is held back, want it to wait", err)
	default:
	}

	close(release)
	select {
	case err := <-done:
		if err != nil {
			t.Errorf("Collect() error = %v, want nil", err)
		}
	case <-time.After(barrierGuard):
		t.Fatal("Collect did not finish after the barrier returned")
	}
	if !fileExists(path) {
		t.Error("the state file is missing after the barrier returned, want the save to follow it")
	}
}

func TestStore_FlushSyncsBarrierFirst(t *testing.T) {
	s, _, path := barrierStore(t, &logCapture{})
	calls := 0
	var existedAtCall bool
	var remaining time.Duration
	var hasDeadline bool
	s.SetBarrier(func(ctx context.Context) error {
		calls++
		existedAtCall = fileExists(path)
		var deadline time.Time
		deadline, hasDeadline = ctx.Deadline()
		remaining = time.Until(deadline)
		return nil
	})

	if err := s.Flush(t0); err != nil {
		t.Fatalf("Flush() error = %v, want nil", err)
	}

	if calls != 1 {
		t.Fatalf("the barrier was called %d times by Flush, want 1", calls)
	}
	if existedAtCall {
		t.Error("the state file existed when the barrier ran, want the barrier before the write")
	}
	if !hasDeadline || remaining <= time.Second || remaining > flushWait {
		t.Errorf("barrier context: deadline %v, remaining %v, want a deadline within %v", hasDeadline, remaining, flushWait)
	}
	if !fileExists(path) {
		t.Error("Flush did not write the state file after the barrier")
	}
}

func TestStore_BarrierRunsBeforeEverySave(t *testing.T) {
	s, sv, _ := barrierStore(t, &logCapture{})
	calls := 0
	s.SetBarrier(func(context.Context) error {
		calls++
		return nil
	})

	collect(t, sv, t0)
	update(t, s, true, func(st *State) { st.Journal.Seqnum = 2 })
	collect(t, sv, t0.Add(10*time.Second))
	update(t, s, false, func(st *State) { st.Journal.Seqnum = 3 })
	if err := s.Flush(t0.Add(11 * time.Second)); err != nil {
		t.Fatalf("Flush() error = %v, want nil", err)
	}

	if calls != 3 {
		t.Errorf("the barrier was called %d times for 2 state job saves and 1 Flush, want 3", calls)
	}
}

// notSavedWarnings returns the captured "agent state not saved" lines.
func notSavedWarnings(t *testing.T, lc *logCapture) []logLine {
	t.Helper()
	var warnings []logLine
	for _, l := range lc.lines(t) {
		if l.Msg == warnNotSaved {
			warnings = append(warnings, l)
		}
	}
	return warnings
}

// requireNotDurable checks the log and the error of a save whose barrier failed with secret.
func requireNotDurable(t *testing.T, lc *logCapture, err error, secret string, returned bool) {
	t.Helper()
	warnings := notSavedWarnings(t, lc)
	if len(warnings) != 1 || warnings[0].Level != "WARN" || warnings[0].Err != reasonNotDurable {
		t.Errorf("warnings %q = %+v, want exactly one WARN with err %q", warnNotSaved, warnings, reasonNotDurable)
	}
	if strings.Contains(fmt.Sprint(err), secret) || lc.contains(secret) {
		t.Errorf("the barrier's own error %q leaked into the returned error %v or the log", secret, err)
	}
	if returned && (err == nil || err.Error() != reasonNotDurable) {
		t.Errorf("Flush() error = %v, want %q", err, reasonNotDurable)
	}
}

func TestStore_FailingBarrierSkipsTheWrite(t *testing.T) {
	const secret = "disk /srv/secret-spool exploded"
	tests := []struct {
		name string
		save func(s *Store, sv *Saver) error
	}{
		{"StateJob", func(_ *Store, sv *Saver) error {
			_, err := sv.Collect(context.Background(), scheduler.Run{Start: t0})
			return err
		}},
		{"Flush", func(s *Store, _ *Saver) error { return s.Flush(t0) }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			lc := &logCapture{}
			s, sv, path := barrierStore(t, lc)
			healthy := false
			s.SetBarrier(func(context.Context) error {
				if healthy {
					return nil
				}
				return errors.New(secret)
			})

			err := tt.save(s, sv)

			if fileExists(path) {
				t.Error("the state file exists after a failed barrier, want no write")
			}
			requireNotDurable(t, lc, err, secret, tt.name == "Flush")

			healthy = true
			if err := s.Flush(t0.Add(time.Minute)); err != nil {
				t.Fatalf("Flush() after the barrier recovered error = %v, want nil", err)
			}
			if !fileExists(path) {
				t.Error("the skipped state was not kept dirty, want it written once the barrier succeeds")
			}
		})
	}
}

func TestStore_NilBarrierRemovesIt(t *testing.T) {
	s, _, path := barrierStore(t, &logCapture{})
	calls := 0
	s.SetBarrier(func(context.Context) error {
		calls++
		return errors.New("must not run")
	})
	s.SetBarrier(nil)

	if err := s.Flush(t0); err != nil {
		t.Fatalf("Flush() after SetBarrier(nil) error = %v, want nil", err)
	}

	if calls != 0 {
		t.Errorf("the removed barrier was called %d times, want 0", calls)
	}
	if !fileExists(path) {
		t.Error("the state file is missing, want the save to run without a barrier")
	}
}
