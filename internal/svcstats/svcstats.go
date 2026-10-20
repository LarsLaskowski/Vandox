// Package svcstats collects the state of the tracked systemd service units of the monitored server through the system
// D-Bus: the active and sub state, the time of the last activation and the restart counter, recorded on change.
package svcstats

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sdbus"
)

// Name, bounds and cadence of the systemd collector.
const (
	Name       = "systemd" // collector name and source of every record
	MaxTracked = 256       // tracked units, exact entries and pattern matches together
	Repeat     = time.Hour // age of the last record of a unit after which it is recorded again
)

// Unit is the state of one unit as the reader returns it.
type Unit = sdbus.Unit

// Reader reads unit states from one open connection to systemd.
type Reader interface {
	// List returns the units matching patterns with their states; ActiveEnter and Restarts are not read.
	List(ctx context.Context, patterns []string) ([]Unit, error)
	// Read returns one Unit per name, in order.
	Read(ctx context.Context, names []string) ([]Unit, error)
	// Close closes the connection.
	Close() error
}

// Systemd opens readers.
type Systemd interface {
	// Open opens a connection to systemd for one run.
	Open(ctx context.Context) (Reader, error)
}

// Sources are the inputs of the collector; a nil field takes its default.
type Sources struct {
	Systemd Systemd // default BusSystemd(sdbus.SystemBusPath)
}

// Collector collects the unit states; it implements scheduler.Collector.
type Collector struct {
	src      Sources
	exact    []string // the entries without a pattern character, in file order
	patterns []string // the entries with a pattern character
	schedule scheduler.Schedule
	log      *slog.Logger

	mu       sync.Mutex
	tracked  []string            // exact entries, then the pattern matches in the order they were first tracked
	isTaken  map[string]struct{} // the names in tracked
	last     map[string]recorded // by primary name: the last record of each unit
	refused  map[string]struct{} // primary names whose state the wire format refused in the last run that saw them
	synced   bool                // a run has succeeded since the start
	warnings warned
}

// recorded is the last record of a unit.
type recorded struct {
	state model.ServiceState
	at    time.Time
}

// warned is which once-per-start lines were logged.
type warned struct {
	missing, cut, notTracked bool
}

// BusSystemd returns the Systemd that talks to the system bus at path.
func BusSystemd(path string) Systemd {
	return busSystemd{path: path}
}

// busSystemd opens sessions on the system bus socket at path.
type busSystemd struct {
	path string
}

// Open dials the bus.
func (b busSystemd) Open(ctx context.Context) (Reader, error) {
	session, err := sdbus.Dial(ctx, b.path)
	if err != nil {
		return nil, err
	}

	return session, nil
}

// New returns the collector tracking units (full names or patterns), run on schedule; a nil log logs nothing.
func New(src Sources, units []string, schedule scheduler.Schedule, log *slog.Logger) *Collector {
	if src.Systemd == nil {
		src.Systemd = BusSystemd(sdbus.SystemBusPath)
	}

	if log == nil {
		log = slog.New(slog.DiscardHandler)
	}

	c := &Collector{
		src:      src,
		schedule: schedule,
		log:      log.With("collector", Name),
		isTaken:  map[string]struct{}{},
		last:     map[string]recorded{},
		refused:  map[string]struct{}{},
	}

	for _, unit := range units {
		if strings.ContainsAny(unit, "*?") {
			c.patterns = append(c.patterns, unit)
		} else {
			c.exact = append(c.exact, unit)
			c.track(unit)
		}
	}

	return c
}

// track adds name to the tracked units.
func (c *Collector) track(name string) {
	if _, taken := c.isTaken[name]; taken {
		return
	}

	c.isTaken[name] = struct{}{}
	c.tracked = append(c.tracked, name)
}

// Name returns Name.
func (c *Collector) Name() string {
	return Name
}

// Collect reads the tracked units and returns the records of the units that changed.
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	reader, err := c.src.Systemd.Open(ctx)
	if err != nil {
		return nil, fmt.Errorf("svcstats: open: %w", err)
	}

	defer func() { _ = reader.Close() }()

	if len(c.patterns) > 0 {
		matches, listErr := reader.List(ctx, c.patterns)
		if listErr != nil {
			return nil, fmt.Errorf("svcstats: list: %w", listErr)
		}

		c.trackMatches(matches)
	}

	units, err := reader.Read(ctx, c.tracked)
	if err != nil {
		return nil, fmt.Errorf("svcstats: read: %w", err)
	}

	if len(units) != len(c.tracked) {
		return nil, errors.New("svcstats: read: unexpected number of units")
	}

	c.warnMissing(units)

	records := c.records(run, units)
	c.synced = true

	return records, nil
}

// trackMatches adds the new matches whose name the wire format accepts and that are not stubs, up to MaxTracked.
func (c *Collector) trackMatches(matches []Unit) {
	candidates := make([]string, 0, len(matches))
	notTracked := 0

	for _, m := range matches {
		_, taken := c.isTaken[m.ID]

		switch {
		case taken || m.LoadState == loadNotFound:
		case wireAcceptsName(m.ID):
			candidates = append(candidates, m.ID)
		default:
			notTracked++
		}
	}

	slices.Sort(candidates)
	candidates = slices.Compact(candidates)

	leftOut := 0

	for _, name := range candidates {
		if len(c.tracked) >= MaxTracked {
			leftOut++

			continue
		}

		c.track(name)
	}

	if notTracked > 0 && !c.warnings.notTracked {
		c.warnings.notTracked = true
		c.log.Warn("systemd units not tracked", "count", notTracked)
	}

	if leftOut > 0 && !c.warnings.cut {
		c.warnings.cut = true
		c.log.Warn("systemd units cut", "left_out", leftOut)
	}
}

// loadNotFound is the load state of a unit systemd has no file for.
const loadNotFound = "not-found"

// wireAcceptsName reports whether the wire format accepts name as a unit name.
func wireAcceptsName(name string) bool {
	probe := model.ServiceState{Unit: name, LoadState: "loaded", ActiveState: "active"}

	return probe.Validate() == nil
}

// warnMissing logs once per start the exact entries systemd has no file for.
func (c *Collector) warnMissing(units []Unit) {
	if c.warnings.missing {
		return
	}

	c.warnings.missing = true

	var missing []string

	for i := range c.exact {
		if units[i].LoadState == loadNotFound {
			missing = append(missing, c.exact[i])
		}
	}

	if len(missing) > 0 {
		c.log.Warn("systemd units not found", "count", len(missing), "units", strings.Join(missing, ","))
	}
}

// records returns the records of the units to record in this run, and the run's own gap when a unit was refused.
func (c *Collector) records(run scheduler.Run, units []Unit) []model.Record {
	var (
		out     []model.Record
		seen    = make(map[string]struct{}, len(units))
		refused bool
		all     = run.PreviousLost || !c.synced
	)

	for i, u := range units {
		if _, twice := seen[u.ID]; twice {
			continue
		}

		seen[u.ID] = struct{}{}

		state := stateOf(u)
		if err := state.Validate(); err != nil {
			c.refuse(c.tracked[i], u.ID, err)

			refused = true

			continue
		}

		if _, was := c.refused[u.ID]; was {
			delete(c.refused, u.ID)
			c.log.Info("systemd unit state recorded again", "unit", c.tracked[i])
		}

		if prev, ok := c.last[u.ID]; !all && ok && sameState(prev, state) && run.Start.Sub(prev.at) < Repeat {
			continue
		}

		c.last[u.ID] = recorded{state: state, at: run.Start}
		out = append(out, model.Record{Meta: model.Meta{Source: Name, CapturedAt: run.Start.UTC()}, Data: &state})
	}

	if refused {
		out = append(out, c.gap(run))
	}

	return out
}

// sameState reports whether the five fields the collector records are those of prev.
func sameState(prev recorded, s model.ServiceState) bool {
	return prev.state.LoadState == s.LoadState && prev.state.ActiveState == s.ActiveState && prev.state.SubState == s.SubState &&
		prev.state.ActiveEnterAt.Equal(s.ActiveEnterAt) && prev.state.Restarts == s.Restarts
}

// refuse forgets the last record of a unit the wire format refuses and logs the field once, never the value.
func (c *Collector) refuse(tracked, id string, err error) {
	delete(c.last, id)

	if _, was := c.refused[id]; was {
		return
	}

	c.refused[id] = struct{}{}

	field := "unknown"

	var fieldErr *model.FieldError
	if errors.As(err, &fieldErr) {
		field = fieldErr.Field
	}

	c.log.Warn("systemd unit state not recorded", "unit", tracked, "field", field)
}

// gap returns the collector's own gap for the run: a unit was left out.
func (c *Collector) gap(run scheduler.Run) model.Record {
	interval := c.schedule.Interval
	if run.Pressure && c.schedule.PressureInterval > 0 {
		interval = c.schedule.PressureInterval
	}

	start := run.Start.UTC()

	return model.Record{
		Meta: model.Meta{Source: scheduler.Source, CapturedAt: start},
		Data: &model.Gap{From: start, To: start.Add(interval), Cause: model.GapUnknown, Collector: Name},
	}
}

// stateOf returns the wire payload of u; an activation time that is 0 or cannot be stored is left out.
func stateOf(u Unit) model.ServiceState {
	state := model.ServiceState{
		Unit: u.ID, LoadState: u.LoadState, ActiveState: u.ActiveState, SubState: u.SubState, Restarts: u.Restarts,
	}

	if u.ActiveEnter > 0 && u.ActiveEnter <= math.MaxInt64/1000 {
		state.ActiveEnterAt = time.UnixMicro(int64(u.ActiveEnter)).UTC()
	}

	return state
}
