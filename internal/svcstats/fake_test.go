package svcstats

import (
	"context"
	"errors"
	"log/slog"
	"path"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// epoch is the start of the first run of every test; enterMicros is a real-looking ActiveEnterTimestamp.
var epoch = time.Date(2026, time.October, 17, 12, 0, 0, 0, time.UTC)

const enterMicros = 1792240371069328 // 2026-10-17T12:32:51.069328Z

// testSchedule is the schedule the agent gives the collector: 30 s, 5 s, 10 s under memory pressure.
var testSchedule = scheduler.Schedule{Interval: 30 * time.Second, Timeout: 5 * time.Second, PressureInterval: 10 * time.Second}

// Stages of a run at which the fake reader fails or hangs.
const (
	stageOpen = "open"
	stageList = "list"
	stageRead = "read"
)

// fault is a one-shot failure of the next run: the stage, and either the error to return or a hang until the context ends.
type fault struct {
	stage string
	err   error
	hang  bool
}

// fakeSystemd is a hand-written systemd: scripted unit states, errors and hangs, and a record of what was asked. It models no
// real component's timing or error behavior; what the real reader does on the bus is the sdbus package's measured fake bus.
type fakeSystemd struct {
	mu       sync.Mutex
	known    map[string]Unit // by every name systemd answers to (primary name and aliases); the value carries the primary ID
	loaded   []string        // primary names List returns (stubs included), in the order they became loaded
	pending  *fault
	opens    int
	closes   int
	patterns [][]string
	reads    [][]string
}

func newFakeSystemd() *fakeSystemd {
	return &fakeSystemd{known: map[string]Unit{}}
}

// add makes systemd list u, answering to its ID and the aliases.
func (f *fakeSystemd) add(u Unit, aliases ...string) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.known[u.ID] = u
	for _, alias := range aliases {
		f.known[alias] = u
	}
	if !slices.Contains(f.loaded, u.ID) {
		f.loaded = append(f.loaded, u.ID)
	}
}

// change applies fn to the unit with the primary name id.
func (f *fakeSystemd) change(id string, fn func(*Unit)) {
	f.mu.Lock()
	defer f.mu.Unlock()

	u := f.known[id]
	fn(&u)
	f.known[id] = u
}

// unload makes systemd forget the unit: no longer listed, answered as not-found.
func (f *fakeSystemd) unload(id string) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.loaded = slices.DeleteFunc(f.loaded, func(name string) bool { return name == id })
	f.known[id] = missing(id)
}

// failNext makes the next run fail at stage with err.
func (f *fakeSystemd) failNext(stage string, err error) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.pending = &fault{stage: stage, err: err}
}

// hangNext makes the next run block at stage until its context ends.
func (f *fakeSystemd) hangNext(stage string) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.pending = &fault{stage: stage, hang: true}
}

// counts returns how often the reader was opened and closed.
func (f *fakeSystemd) counts() (opens, closes int) {
	f.mu.Lock()
	defer f.mu.Unlock()

	return f.opens, f.closes
}

// listed returns the patterns of every List call.
func (f *fakeSystemd) listed() [][]string {
	f.mu.Lock()
	defer f.mu.Unlock()

	return slices.Clone(f.patterns)
}

// readNames returns the names of every Read call.
func (f *fakeSystemd) readNames() [][]string {
	f.mu.Lock()
	defer f.mu.Unlock()

	return slices.Clone(f.reads)
}

// Open implements Opener.
func (f *fakeSystemd) Open(ctx context.Context) (Reader, error) {
	f.mu.Lock()
	f.opens++
	fl := f.pending
	f.pending = nil
	f.mu.Unlock()

	r := &fakeReader{f: f, fault: fl}
	if err := r.trip(ctx, stageOpen); err != nil {
		return nil, err
	}
	return r, nil
}

// fakeReader is one open connection of the fake.
type fakeReader struct {
	f     *fakeSystemd
	fault *fault
}

// trip fails or hangs when the run's fault is at stage.
func (r *fakeReader) trip(ctx context.Context, stage string) error {
	if r.fault == nil || r.fault.stage != stage {
		return nil
	}
	if r.fault.hang {
		<-ctx.Done()
		return ctx.Err()
	}
	return r.fault.err
}

// List implements Reader: the loaded units whose ID matches a pattern, in reverse listing order, without ActiveEnter and Restarts.
func (r *fakeReader) List(ctx context.Context, patterns []string) ([]Unit, error) {
	r.f.mu.Lock()
	r.f.patterns = append(r.f.patterns, slices.Clone(patterns))
	r.f.mu.Unlock()
	if err := r.trip(ctx, stageList); err != nil {
		return nil, err
	}

	r.f.mu.Lock()
	defer r.f.mu.Unlock()
	var out []Unit
	for _, id := range slices.Backward(r.f.loaded) {
		if !matchesAny(patterns, id) {
			continue
		}
		u := r.f.known[id]
		out = append(out, Unit{ID: u.ID, LoadState: u.LoadState, ActiveState: u.ActiveState, SubState: u.SubState})
	}
	return out, nil
}

func matchesAny(patterns []string, id string) bool {
	return slices.ContainsFunc(patterns, func(p string) bool {
		ok, err := path.Match(p, id)
		return err == nil && ok
	})
}

// Read implements Reader: one Unit per name, in order; a name systemd does not know is not-found and inactive.
func (r *fakeReader) Read(ctx context.Context, names []string) ([]Unit, error) {
	r.f.mu.Lock()
	r.f.reads = append(r.f.reads, slices.Clone(names))
	r.f.mu.Unlock()
	if err := r.trip(ctx, stageRead); err != nil {
		return nil, err
	}

	r.f.mu.Lock()
	defer r.f.mu.Unlock()
	out := make([]Unit, 0, len(names))
	for _, name := range names {
		u, ok := r.f.known[name]
		if !ok {
			u = missing(name)
		}
		out = append(out, u)
	}
	return out, nil
}

// Close implements Reader.
func (r *fakeReader) Close() error {
	r.f.mu.Lock()
	defer r.f.mu.Unlock()

	r.f.closes++
	return nil
}

// running returns a running service that was activated at enterMicros.
func running(id string) Unit {
	return Unit{ID: id, LoadState: "loaded", ActiveState: "active", SubState: "running", ActiveEnter: enterMicros}
}

// missing returns the unit systemd answers for a name it has no file for.
func missing(id string) Unit {
	return Unit{ID: id, LoadState: "not-found", ActiveState: "inactive", SubState: "dead"}
}

// logEntry is one logged line.
type logEntry struct {
	level slog.Level
	msg   string
	attrs map[string]string
}

// logStore keeps the lines of a recording logger.
type logStore struct {
	mu      sync.Mutex
	entries []logEntry
}

func (s *logStore) add(e logEntry) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.entries = append(s.entries, e)
}

func (s *logStore) all() []logEntry {
	s.mu.Lock()
	defer s.mu.Unlock()

	return slices.Clone(s.entries)
}

// find returns the lines with the message msg.
func (s *logStore) find(msg string) []logEntry {
	var out []logEntry
	for _, e := range s.all() {
		if e.msg == msg {
			out = append(out, e)
		}
	}
	return out
}

// text returns every message, attribute key and attribute value of every line, for leak checks.
func (s *logStore) text() string {
	var b strings.Builder
	for _, e := range s.all() {
		b.WriteString(e.msg)
		b.WriteByte('\n')
		for k, v := range e.attrs {
			b.WriteString(k + "=" + v + "\n")
		}
	}
	return b.String()
}

// recordingHandler is a slog.Handler that keeps what it is given, with the attributes of WithAttrs.
type recordingHandler struct {
	store  *logStore
	preset []slog.Attr
}

func (h *recordingHandler) Enabled(context.Context, slog.Level) bool { return true }

func (h *recordingHandler) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{level: r.Level, msg: r.Message, attrs: map[string]string{}}
	for _, a := range h.preset {
		e.attrs[a.Key] = a.Value.String()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.attrs[a.Key] = a.Value.String()
		return true
	})
	h.store.add(e)
	return nil
}

func (h *recordingHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &recordingHandler{store: h.store, preset: append(slices.Clone(h.preset), attrs...)}
}

func (h *recordingHandler) WithGroup(string) slog.Handler { return h }

// wantLogged checks that exactly n lines with the message msg and level were logged, each from the collector, and returns them.
func wantLogged(t *testing.T, store *logStore, level slog.Level, msg string, n int) []logEntry {
	t.Helper()

	found := store.find(msg)
	if len(found) != n {
		t.Errorf("log holds %d lines %q, want %d; log:\n%s", len(found), msg, n, store.text())
	}
	for _, e := range found {
		if e.level != level {
			t.Errorf("line %q has level %v, want %v", msg, e.level, level)
		}
		if e.attrs["collector"] != Name {
			t.Errorf("line %q has collector = %q, want %q", msg, e.attrs["collector"], Name)
		}
	}
	return found
}

// rig is a collector over a fake systemd and a recording log.
type rig struct {
	t    *testing.T
	fake *fakeSystemd
	logs *logStore
	c    *Collector
}

// newRig returns a collector tracking units over a fake systemd.
func newRig(t *testing.T, units ...string) *rig {
	t.Helper()

	fake := newFakeSystemd()
	logs := &logStore{}
	c := New(Sources{Systemd: fake}, units, testSchedule, slog.New(&recordingHandler{store: logs}))
	return &rig{t: t, fake: fake, logs: logs, c: c}
}

// runAt returns the run that starts offset after epoch.
func runAt(offset time.Duration, lost bool) scheduler.Run {
	return scheduler.Run{Start: epoch.Add(offset), PreviousLost: lost}
}

// outcome is the records of one run, sorted by what they are.
type outcome struct {
	states map[string]*model.ServiceState // by unit name
	gaps   []*model.Gap
	count  int
}

// collect runs the collector once and fails the test on an error.
func (r *rig) collect(run scheduler.Run) outcome {
	r.t.Helper()

	recs, err := r.c.Collect(context.Background(), run)
	if err != nil {
		r.t.Fatalf("Collect(%v) error = %v, want nil", run.Start.Sub(epoch), err)
	}
	return sortRecords(r.t, run, recs)
}

// sortRecords splits the records of a run into service states and gaps, checking their metadata.
func sortRecords(t *testing.T, run scheduler.Run, recs []model.Record) outcome {
	t.Helper()

	out := outcome{states: map[string]*model.ServiceState{}, count: len(recs)}
	for _, rec := range recs {
		switch data := rec.Data.(type) {
		case *model.ServiceState:
			addState(t, &out, run, rec, data)
		case *model.Gap:
			if rec.Source != scheduler.Source {
				t.Errorf("gap source = %q, want %q", rec.Source, scheduler.Source)
			}
			out.gaps = append(out.gaps, data)
		default:
			t.Errorf("record payload = %T, want *model.ServiceState or *model.Gap", rec.Data)
		}
	}
	return out
}

func addState(t *testing.T, out *outcome, run scheduler.Run, rec model.Record, state *model.ServiceState) {
	t.Helper()

	if rec.Source != Name {
		t.Errorf("record of %q: source = %q, want %q", state.Unit, rec.Source, Name)
	}
	if !rec.CapturedAt.Equal(run.Start) {
		t.Errorf("record of %q: captured_at = %v, want the run's start %v", state.Unit, rec.CapturedAt, run.Start)
	}
	if err := state.Validate(); err != nil {
		t.Errorf("record of %q: Validate() = %v, want nil", state.Unit, err)
	}
	if _, twice := out.states[state.Unit]; twice {
		t.Errorf("unit %q recorded twice in one run", state.Unit)
	}
	out.states[state.Unit] = state
}

// units returns the recorded unit names, sorted.
func (o outcome) units() []string {
	names := make([]string, 0, len(o.states))
	for name := range o.states {
		names = append(names, name)
	}
	slices.Sort(names)
	return names
}

// wantUnits checks that exactly the named units were recorded and nothing else (no gap).
func (o outcome) wantUnits(t *testing.T, what string, want ...string) {
	t.Helper()

	slices.Sort(want)
	if got := o.units(); !slices.Equal(got, want) {
		t.Errorf("%s: recorded units %v, want %v", what, got, want)
	}
	if len(o.gaps) != 0 || o.count != len(want) {
		t.Errorf("%s: %d records and %d gaps, want %d records and no gap", what, o.count, len(o.gaps), len(want))
	}
}

var errBus = errors.New("bus is away")
