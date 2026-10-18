package sysstats

import (
	"context"
	"log/slog"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// sample is everything one system run read.
type sample struct {
	start time.Time
	cpu   cpuCounters
	load  [3]float64
	mem   *memory
	psi   []pressureFile
	vm    [4]uint64
	disks []diskSample
}

// System collects CPU, load, memory, pressure, vmstat and disk metrics; it implements scheduler.Collector.
type System struct {
	src Sources
	log *slog.Logger

	mu          sync.Mutex
	prev        *sample // the last successful sample
	unavailable string  // the pressure resources last logged as unavailable, comma-separated
	onSwitch    func(high bool)
	high        bool // whether memory pressure mode is on; it starts off
}

// NewSystem returns the system collector reading src; a nil log logs nothing.
func NewSystem(src Sources, log *slog.Logger) *System {
	return &System{src: src.withDefaults(), log: logger(log)}
}

// SetPressureSwitch sets f, which is called with the new state whenever memory pressure mode changes after a sample.
func (c *System) SetPressureSwitch(f func(high bool)) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.onSwitch = f
}

// Name returns SystemName.
func (c *System) Name() string {
	return SystemName
}

// Collect takes one system sample. A source that cannot be read or does not parse fails the sample.
func (c *System) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	c.mu.Lock()
	prev, loggedUnavailable := c.prev, c.unavailable
	c.mu.Unlock()

	s, unavailable, err := c.read(run)
	if err != nil {
		return nil, err
	}
	records := s.records(run, prev)

	c.mu.Lock()
	c.prev, c.unavailable = s, unavailable
	c.mu.Unlock()
	c.logPressure(loggedUnavailable, unavailable)
	c.updatePressureMode(s)
	return records, nil
}

// updatePressureMode decides the memory pressure mode after the successful sample s and, when it changes, logs one line
// and calls the switch.
func (c *System) updatePressureMode(s *sample) {
	reading := PressureReading{
		MemAvailable: kBToBytes(s.mem.values["MemAvailable"]),
		MemTotal:     kBToBytes(s.mem.values["MemTotal"]),
	}
	for _, f := range s.psi {
		if f.resource == "memory" && len(f.lines) > 0 && f.lines[0].kind == "some" {
			reading.Avg10, reading.HasAvg10 = f.lines[0].avg[0], true
		}
	}

	c.mu.Lock()
	next := NextPressure(c.high, reading)
	changed := next != c.high
	c.high = next
	onSwitch := c.onSwitch
	c.mu.Unlock()
	if !changed {
		return
	}

	msg := "memory pressure mode off"
	if next {
		msg = "memory pressure mode on"
	}
	attrs := []any{"collector", SystemName, "mem_available_bytes", reading.MemAvailable}
	if reading.HasAvg10 {
		attrs = append(attrs, "memory_some_avg10", reading.Avg10)
	}
	c.log.Info(msg, attrs...)
	if onSwitch != nil {
		onSwitch(next)
	}
}

// kBToBytes returns kB kibibytes in bytes; a negative value is 0. The value of a meminfo line is at most maxKBValue, so the
// product fits.
func kBToBytes(kB int64) uint64 {
	if kB < 0 || kB > maxKBValue {
		return 0
	}
	return uint64(kB) * 1024
}

// read reads the sources in the order of the records. unavailable names the pressure resources the host does not offer.
func (c *System) read(run scheduler.Run) (s *sample, unavailable string, err error) {
	s = &sample{start: run.Start}
	if s.cpu, err = readStat(c.src.Proc); err != nil {
		return nil, "", err
	}
	if s.load, err = readLoadavg(c.src.Proc); err != nil {
		return nil, "", err
	}
	if s.mem, err = readMeminfo(c.src.Proc); err != nil {
		return nil, "", err
	}
	if s.vm, err = readVmstat(c.src.Proc); err != nil {
		return nil, "", err
	}
	var missing []string
	if s.psi, missing, err = readPressure(c.src.Proc); err != nil {
		return nil, "", err
	}
	if s.disks, err = readDisks(c.src.Sys); err != nil {
		return nil, "", err
	}
	return s, strings.Join(missing, ","), nil
}

// logPressure logs a change of the set of unavailable pressure resources: a warning naming the set when it is not empty, an
// info line when it becomes empty.
func (c *System) logPressure(was, now string) {
	switch {
	case was == now:
	case now == "":
		c.log.Info("pressure stall information available", "collector", SystemName)
	default:
		c.log.Warn("pressure stall information unavailable", "collector", SystemName, "resources", now)
	}
}

// records returns the records of the sample in the order of the Agent area; prev is the previous successful sample or nil.
func (s *sample) records(run scheduler.Run, prev *sample) []model.Record {
	r := &recorder{run: run}
	dt := 0.0
	if prev != nil && s.start.After(prev.start) {
		dt = s.start.Sub(prev.start).Seconds()
	}
	if dt > 0 {
		s.addCPURates(r, prev)
	}
	for i, name := range []string{"load1", "load5", "load15"} {
		r.add(name, s.load[i], "")
	}
	s.mem.addTo(r)
	for _, f := range s.psi {
		f.addTo(r)
	}
	s.addVmstat(r, prev, dt)
	for _, d := range s.disks {
		d.addTo(r, prev.diskCounters(d.name, dt), dt)
	}
	return r.records
}

// addCPURates adds cpu_percent per mode: the increase of the mode over the increase of the sum of the 8 counters. There
// is none when a counter fell or the sum did not grow.
func (s *sample) addCPURates(r *recorder, prev *sample) {
	var delta [len(s.cpu)]float64
	sum := 0.0
	for i := range s.cpu {
		if s.cpu[i] < prev.cpu[i] {
			return
		}
		delta[i] = float64(s.cpu[i] - prev.cpu[i])
		sum += delta[i]
	}
	if sum == 0 {
		return
	}
	for _, m := range cpuModes {
		r.add("cpu_percent", delta[m.index]/sum*100, "percent", "mode", m.name)
	}
}

// addVmstat adds the four totals and, with a predecessor and dt > 0, their rates.
func (s *sample) addVmstat(r *recorder, prev *sample, dt float64) {
	for i, name := range vmstatCounters {
		r.add("vmstat_total", float64(s.vm[i]), "", "counter", name)
	}
	if dt <= 0 {
		return
	}
	for i, name := range vmstatCounters {
		if v, ok := rate(s.vm[i], prev.vm[i], dt); ok {
			r.add("vmstat_per_second", v, "per_second", "counter", name)
		}
	}
}

// diskCounters returns the counters of the disk name in s, or nil when s is nil, dt is not positive or s has no such disk.
func (s *sample) diskCounters(name string, dt float64) *diskCounters {
	if s == nil || dt <= 0 {
		return nil
	}
	i := slices.IndexFunc(s.disks, func(d diskSample) bool { return d.name == name })
	if i < 0 {
		return nil
	}
	return &s.disks[i].diskCounters
}
