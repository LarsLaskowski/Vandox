// Package backups collects the backup runs of the monitored server: it watches the processes the configured rules
// name and writes the start and the end of each run.
package backups

import (
	"context"
	"fmt"
	"io/fs"
	"log/slog"
	"strings"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/agentstate"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Name is the collector name and the source of every record.
const Name = "backups"

// Rule names the processes of a backup: a process matches when its command equals Command or its cleaned command
// line starts with CmdlinePrefix.
type Rule struct {
	Command       string
	CmdlinePrefix string
}

// endAfter is how long no matching process may be seen before the run ends.
const endAfter = 60 * time.Second

// Backups is the backup run collector.
type Backups struct {
	proc    fs.FS
	rules   []Rule
	store   *agentstate.Store
	log     *slog.Logger
	reader  *procstats.CommandReader
	checked bool      // whether the stored run was compared with the processes yet
	goneBy  time.Time // the first check that saw no matching process since the last match, zero while one is seen
	pending *change   // what the latest check decided, applied when the scheduler reports its records kept
}

// New returns the collector that reads the processes below proc, matches them against rules and keeps the open
// run in store.
func New(proc fs.FS, rules []Rule, store *agentstate.Store, log *slog.Logger) *Backups {
	return &Backups{proc: proc, rules: rules, store: store, log: log, reader: procstats.NewCommandReader(proc)}
}

// Name returns the collector name.
func (b *Backups) Name() string {
	return Name
}

// Kept applies the change of the check run when the scheduler reports its records kept and forgets it otherwise.
func (b *Backups) Kept(run scheduler.Run, kept bool) {
	pending := b.pending
	b.pending = nil
	if kept && pending != nil && pending.start.Equal(run.Start.UTC()) {
		b.apply(pending)
	}
}

// change is what one check decided for the collector's state.
type change struct {
	start  time.Time           // the check's run start, matched by Kept
	open   *agentstate.OpenRun // the open run after the check, nil for none
	write  bool                // whether the state's open run changes
	urgent bool                // a start, an end or a higher process count
	goneBy time.Time           // the gone-by time after the check
}

// apply writes the change into the state and the collector.
func (b *Backups) apply(c *change) {
	if c.write {
		open := copyRun(c.open)
		b.store.Update(c.urgent, func(st *agentstate.State) {
			st.Backup = open
		})
	}
	b.goneBy, b.checked = c.goneBy, true
}

// sighting is what one check found: the number of matching processes and the earliest of them.
type sighting struct {
	count    uint32
	earliest procstats.ProcessInfo
}

// Collect checks the processes and returns the backup_run records of the run.
func (b *Backups) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	if len(b.rules) == 0 {
		return nil, nil
	}
	b.pending = nil
	bootID, err := procstats.ReadBootID(b.proc)
	if err != nil {
		return nil, fmt.Errorf("backups: %w", err)
	}
	boot, err := procstats.ReadBootTime(b.proc)
	if err != nil {
		return nil, fmt.Errorf("backups: %w", err)
	}
	found, err := b.scan(ctx, boot)
	if err != nil {
		return nil, err
	}

	records, c := b.advance(run.Start.UTC(), bootID, found)
	c.start = run.Start.UTC()
	b.pending = &c

	return records, nil
}

// copyRun returns a copy of run, or nil.
func copyRun(run *agentstate.OpenRun) *agentstate.OpenRun {
	if run == nil {
		return nil
	}
	c := *run

	return &c
}

// scan lists the processes and counts those a rule matches.
func (b *Backups) scan(ctx context.Context, boot time.Time) (sighting, error) {
	var found sighting
	pids, _, err := procstats.ListPIDs(b.proc)
	if err != nil {
		return found, fmt.Errorf("backups: %w", err)
	}
	for _, pid := range pids {
		if err := ctx.Err(); err != nil {
			return found, fmt.Errorf("backups: %w", err)
		}
		info, err := b.reader.Process(pid, boot)
		if err != nil || !b.matches(&info) {
			continue
		}
		if found.count == 0 || info.StartedAt.Before(found.earliest.StartedAt) {
			found.earliest = info
		}
		found.count++
	}

	return found, nil
}

// matches reports whether a rule names the process: its command equals Command or its command line starts with
// CmdlinePrefix, both compared literally.
func (b *Backups) matches(info *procstats.ProcessInfo) bool {
	for _, r := range b.rules {
		if r.Command != "" && info.Command == r.Command {
			return true
		}
		if r.CmdlinePrefix != "" && strings.HasPrefix(info.Cmdline, r.CmdlinePrefix) {
			return true
		}
	}

	return false
}

// advance decides the check at now from the committed state and returns the records of the phases it passed and the
// change to the state; nothing is written.
func (b *Backups) advance(now time.Time, bootID string, found sighting) ([]model.Record, change) {
	var records []model.Record
	stored := b.store.State().Backup
	open := copyRun(stored)
	c := change{goneBy: b.goneBy}
	if open != nil && !b.checked && (open.BootID != bootID || found.count == 0) {
		records = append(records, endRecord(open, now))
		c.goneBy, c.write, c.urgent = time.Time{}, true, true
		open = nil
	}

	switch {
	case open == nil && found.count > 0:
		records = append(records, startRecord(bootID, &found))
		started := found.earliest.StartedAt.UTC()
		open = &agentstate.OpenRun{BootID: bootID, StartedAt: started, LastSeen: laterOf(now, started), MaxProcesses: found.count}
		c.goneBy, c.write, c.urgent = time.Time{}, true, true
	case open != nil && found.count > 0:
		c.goneBy, c.write = time.Time{}, true
		c.urgent = c.urgent || found.count > open.MaxProcesses
		open.LastSeen = laterOf(now, open.StartedAt)
		open.MaxProcesses = maxOf(open.MaxProcesses, found.count)
	case open != nil:
		if c.goneBy.IsZero() {
			c.goneBy = now
		}
		if now.Sub(open.LastSeen) >= endAfter {
			records = append(records, endRecord(open, c.goneBy))
			c.goneBy, c.write, c.urgent = time.Time{}, true, true
			open = nil
		}
	}
	c.open = open

	return records, c
}

// startRecord returns the record of the start of the run found.
func startRecord(bootID string, found *sighting) model.Record {
	started := found.earliest.StartedAt.UTC()
	data := &model.BackupRun{
		Phase:     model.BackupPhaseStart,
		BootID:    bootID,
		StartedAt: started,
		PID:       found.earliest.PID,
		Command:   found.earliest.Command,
		Processes: found.count,
	}

	return model.Record{Meta: model.Meta{Source: Name, CapturedAt: started}, Data: data}
}

// endRecord returns the record of the end of run, gone by the check at goneBy.
func endRecord(run *agentstate.OpenRun, goneBy time.Time) model.Record {
	lastSeen := run.LastSeen.UTC()
	data := &model.BackupRun{
		Phase:        model.BackupPhaseEnd,
		BootID:       run.BootID,
		StartedAt:    run.StartedAt.UTC(),
		LastSeenAt:   lastSeen,
		GoneBy:       laterOf(goneBy, lastSeen),
		MaxProcesses: run.MaxProcesses,
	}

	return model.Record{Meta: model.Meta{Source: Name, CapturedAt: lastSeen}, Data: data}
}

// laterOf returns the later of a and b.
func laterOf(a, b time.Time) time.Time {
	if b.After(a) {
		return b
	}

	return a
}

// maxOf returns the larger of a and b.
func maxOf(a, b uint32) uint32 {
	if b > a {
		return b
	}

	return a
}
