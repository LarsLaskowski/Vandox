// Package backups collects the backup runs of the monitored server: it watches the processes the configured rules
// name and writes the start and the end of each run.
package backups

import (
	"context"
	"fmt"
	"io"
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

// maxBootIDBytes bounds the read of the boot ID file.
const maxBootIDBytes = 128

// Backups is the backup run collector.
type Backups struct {
	proc    fs.FS
	rules   []Rule
	store   *agentstate.Store
	log     *slog.Logger
	reader  *procstats.CommandReader
	checked bool      // whether the stored run was compared with the processes yet
	goneBy  time.Time // the first check that saw no matching process since the last match, zero while one is seen
	before  *memory   // what the previous run started with, restored when its result was lost
}

// memory is the state of the collector before a run: the stored open run, goneBy and the first-check flag.
type memory struct {
	open    *agentstate.OpenRun
	goneBy  time.Time
	checked bool
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
	b.remember(run.PreviousLost)
	bootID, err := b.readBootID()
	if err != nil {
		return nil, err
	}
	boot, err := procstats.ReadBootTime(b.proc)
	if err != nil {
		return nil, fmt.Errorf("backups: %w", err)
	}
	found, err := b.scan(ctx, boot)
	if err != nil {
		return nil, err
	}

	return b.advance(run.Start.UTC(), bootID, found), nil
}

// remember takes the copy of the state this run starts with; when the previous run's result was lost, it first
// restores the copy that run took, so the lost check is decided again.
func (b *Backups) remember(previousLost bool) {
	if previousLost && b.before != nil {
		b.goneBy, b.checked = b.before.goneBy, b.before.checked
		if stored := b.store.State().Backup; !sameRun(stored, b.before.open) {
			restored := copyRun(b.before.open)
			b.store.Update(true, func(st *agentstate.State) {
				st.Backup = restored
			})
		}
	}
	b.before = &memory{open: copyRun(b.store.State().Backup), goneBy: b.goneBy, checked: b.checked}
}

// copyRun returns a copy of run, or nil.
func copyRun(run *agentstate.OpenRun) *agentstate.OpenRun {
	if run == nil {
		return nil
	}
	c := *run

	return &c
}

// sameRun reports whether a and b are the same open run, or both none.
func sameRun(a, b *agentstate.OpenRun) bool {
	if a == nil || b == nil {
		return a == b
	}

	return a.BootID == b.BootID && a.StartedAt.Equal(b.StartedAt) && a.LastSeen.Equal(b.LastSeen) && a.MaxProcesses == b.MaxProcesses
}

// readBootID returns the boot ID of the monitored server.
func (b *Backups) readBootID() (string, error) {
	f, err := b.proc.Open("sys/kernel/random/boot_id")
	if err != nil {
		return "", fmt.Errorf("backups: boot id: %w", err)
	}
	defer func() { _ = f.Close() }()
	raw, err := io.ReadAll(io.LimitReader(f, maxBootIDBytes))
	if err != nil {
		return "", fmt.Errorf("backups: boot id: %w", err)
	}

	return strings.TrimSpace(string(raw)), nil
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

// advance moves the open run on by the check at now and returns the records of the phases it passed.
func (b *Backups) advance(now time.Time, bootID string, found sighting) []model.Record {
	var records []model.Record
	open := b.store.State().Backup
	if open != nil && !b.checked && (open.BootID != bootID || found.count == 0) {
		records = append(records, endRecord(open, now))
		b.clear()
		open = nil
	}
	b.checked = true

	switch {
	case open == nil && found.count > 0:
		records = append(records, startRecord(bootID, &found))
		b.begin(bootID, now, &found)
	case open != nil && found.count > 0:
		b.goneBy = time.Time{}
		b.see(now, found.count, open.MaxProcesses)
	case open != nil:
		if b.goneBy.IsZero() {
			b.goneBy = now
		}
		if now.Sub(open.LastSeen) >= endAfter {
			records = append(records, endRecord(open, b.goneBy))
			b.clear()
		}
	}

	return records
}

// begin stores the run that starts with found.
func (b *Backups) begin(bootID string, now time.Time, found *sighting) {
	started := found.earliest.StartedAt.UTC()
	b.goneBy = time.Time{}
	b.store.Update(true, func(st *agentstate.State) {
		st.Backup = &agentstate.OpenRun{BootID: bootID, StartedAt: started, LastSeen: laterOf(now, started), MaxProcesses: found.count}
	})
}

// see stores a check that saw count matching processes.
func (b *Backups) see(now time.Time, count, peak uint32) {
	b.store.Update(count > peak, func(st *agentstate.State) {
		if st.Backup == nil {
			return
		}
		st.Backup.LastSeen = laterOf(now, st.Backup.StartedAt)
		st.Backup.MaxProcesses = maxOf(st.Backup.MaxProcesses, count)
	})
}

// clear removes the open run from the state.
func (b *Backups) clear() {
	b.goneBy = time.Time{}
	b.store.Update(true, func(st *agentstate.State) {
		st.Backup = nil
	})
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
