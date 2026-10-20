package svcstats

import (
	"context"
	"errors"
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"log/slog"
	"maps"
	"math"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

func TestNew_TakesDefaultsAndName(t *testing.T) {
	c := New(Sources{}, []string{"mariadb.service"}, testSchedule, nil)

	if c.src.Systemd == nil || c.log == nil || c.Name() != Name || Name != "systemd" {
		t.Errorf("New(Sources{}, ...) = systemd %v, log %v, name %q, want the system bus, a logger and %q", c.src.Systemd, c.log, c.Name(), "systemd")
	}
}

func TestCollect_FirstRunRecordsEveryUnit(t *testing.T) {
	r := newRig(t, "mariadb.service", "apache2.service", "dovecot.service", "plesk.service", "postfix.service")
	maria := running("mariadb.service")
	maria.Restarts = 2
	r.fake.add(maria)
	r.fake.add(missing("apache2.service"))
	r.fake.add(Unit{ID: "dovecot.service", LoadState: "masked", ActiveState: "inactive", SubState: "dead"})
	r.fake.add(Unit{ID: "plesk.service", LoadState: "bad-setting", ActiveState: "inactive", SubState: "dead"})
	r.fake.add(Unit{ID: "postfix.service", LoadState: "error", ActiveState: "failed", SubState: "failed", Restarts: 7})

	out := r.collect(runAt(0, false))

	out.wantUnits(t, "first run", "mariadb.service", "apache2.service", "dovecot.service", "plesk.service", "postfix.service")
	enter := time.Date(2026, time.October, 17, 12, 32, 51, 69328000, time.UTC)
	want := map[string]model.ServiceState{
		"mariadb.service": {Unit: "mariadb.service", LoadState: "loaded", ActiveState: "active", SubState: "running", ActiveEnterAt: enter, Restarts: 2},
		"apache2.service": {Unit: "apache2.service", LoadState: "not-found", ActiveState: "inactive", SubState: "dead"},
		"dovecot.service": {Unit: "dovecot.service", LoadState: "masked", ActiveState: "inactive", SubState: "dead"},
		"plesk.service":   {Unit: "plesk.service", LoadState: "bad-setting", ActiveState: "inactive", SubState: "dead"},
		"postfix.service": {Unit: "postfix.service", LoadState: "error", ActiveState: "failed", SubState: "failed", Restarts: 7},
	}
	for name, w := range want {
		got := out.states[name]
		if got == nil || got.Unit != w.Unit || got.LoadState != w.LoadState || got.ActiveState != w.ActiveState || got.SubState != w.SubState ||
			!got.ActiveEnterAt.Equal(w.ActiveEnterAt) || got.Restarts != w.Restarts {
			t.Errorf("record of %s = %+v, want %+v", name, got, w)
		}
	}
	if reads := r.fake.readNames(); len(reads) != 1 || !slices.Equal(reads[0], []string{"mariadb.service", "apache2.service", "dovecot.service", "plesk.service", "postfix.service"}) {
		t.Errorf("Read calls = %v, want one with the tracked names in file order", reads)
	}
	if lists := r.fake.listed(); len(lists) != 0 {
		t.Errorf("List calls = %v, want none without a pattern", lists)
	}
	if opens, closes := r.fake.counts(); opens != 1 || closes != 1 {
		t.Errorf("reader opened %d times and closed %d times, want once each", opens, closes)
	}
	r.collect(runAt(30*time.Second, false)).wantUnits(t, "second run without a change")
}

func TestCollect_AliasRecordedOnceUnderPrimaryName(t *testing.T) {
	tests := []struct {
		name  string
		units []string
	}{
		{"two aliases of one unit", []string{"mysql.service", "mariadb.service"}},
		{"an alias and a pattern", []string{"mysql.service", "maria*.service"}},
		{"an exact entry and a pattern", []string{"mariadb.service", "maria*.service"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := newRig(t, tc.units...)
			r.fake.add(running("mariadb.service"), "mysql.service")

			out := r.collect(runAt(0, false))

			out.wantUnits(t, "first run", "mariadb.service")
			r.collect(runAt(30*time.Second, false)).wantUnits(t, "second run")
		})
	}
}

func TestCollect_ActiveEnterLeftOutWhenZeroOrNotStorable(t *testing.T) {
	tests := []struct {
		name  string
		micro uint64
		want  time.Time
	}{
		{"never active", 0, time.Time{}},
		{"all bits set", math.MaxUint64, time.Time{}},
		{"after 2262-04-11", 9223372036854776, time.Time{}},
		{"the last storable microsecond", 9223372036854775, time.Date(2262, time.April, 11, 23, 47, 16, 854775000, time.UTC)},
		{"a real timestamp", enterMicros, time.Date(2026, time.October, 17, 12, 32, 51, 69328000, time.UTC)},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := newRig(t, "mariadb.service")
			u := running("mariadb.service")
			u.ActiveEnter = tc.micro
			r.fake.add(u)

			out := r.collect(runAt(0, false))

			out.wantUnits(t, "first run", "mariadb.service")
			if got := out.states["mariadb.service"].ActiveEnterAt; !got.Equal(tc.want) {
				t.Errorf("active_enter_at for %d us = %v, want %v", tc.micro, got, tc.want)
			}
		})
	}
}

// TestCollect_NotFoundUnitNeedsNoGet checks what the collector passes through for not-found units; that the reader
// reads no properties of a not-found inactive unit is tested in internal/sdbus.
func TestCollect_NotFoundUnitNeedsNoGet(t *testing.T) {
	r := newRig(t, "apache2.service", "gone.service")
	r.fake.add(missing("apache2.service"))
	removedWhileRunning := Unit{ID: "gone.service", LoadState: "not-found", ActiveState: "active", SubState: "running", ActiveEnter: enterMicros, Restarts: 3}
	r.fake.add(removedWhileRunning)

	out := r.collect(runAt(0, false))

	out.wantUnits(t, "first run", "apache2.service", "gone.service")
	if s := out.states["apache2.service"]; s.Restarts != 0 || !s.ActiveEnterAt.IsZero() || s.LoadState != "not-found" {
		t.Errorf("record of a not-found inactive unit = %+v, want restarts 0, no active_enter_at, load_state not-found", s)
	}
	if s := out.states["gone.service"]; s.Restarts != 3 || s.ActiveEnterAt.IsZero() || s.ActiveState != "active" {
		t.Errorf("record of a not-found unit that still runs = %+v, want the reported restarts 3 and active_enter_at", s)
	}
}

func TestCollect_RecordsOnlyChanges(t *testing.T) {
	tests := []struct {
		name   string
		mutate func(*Unit)
		check  func(*model.ServiceState) bool
	}{
		{"load_state", func(u *Unit) { u.LoadState = "masked" }, func(s *model.ServiceState) bool { return s.LoadState == "masked" }},
		{"active_state", func(u *Unit) { u.ActiveState = "failed" }, func(s *model.ServiceState) bool { return s.ActiveState == "failed" }},
		{"sub_state", func(u *Unit) { u.SubState = "start-pre" }, func(s *model.ServiceState) bool { return s.SubState == "start-pre" }},
		{"active_enter_at", func(u *Unit) { u.ActiveEnter += 1_000_000 }, func(s *model.ServiceState) bool {
			return s.ActiveEnterAt.Equal(time.UnixMicro(enterMicros + 1_000_000).UTC())
		}},
		{"restarts", func(u *Unit) { u.Restarts++ }, func(s *model.ServiceState) bool { return s.Restarts == 1 }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := newRig(t, "mariadb.service", "nginx.service")
			r.fake.add(running("mariadb.service"))
			r.fake.add(running("nginx.service"))
			r.collect(runAt(0, false))
			r.collect(runAt(30*time.Second, false)).wantUnits(t, "run without a change")

			r.fake.change("mariadb.service", tc.mutate)
			out := r.collect(runAt(60*time.Second, false))

			out.wantUnits(t, "run after a change of "+tc.name, "mariadb.service")
			if s := out.states["mariadb.service"]; s == nil || !tc.check(s) {
				t.Errorf("record after a change of %s = %+v, want the new value", tc.name, s)
			}
			r.collect(runAt(90*time.Second, false)).wantUnits(t, "run after the change was recorded")
		})
	}
}

// wantRecorded checks whether the run at offset recorded the single tracked unit.
func wantRecorded(t *testing.T, r *rig, offset time.Duration, lost, want bool) {
	t.Helper()

	out := r.collect(runAt(offset, lost))
	if got := len(out.states) == 1; got != want {
		t.Errorf("run at %v (previous lost: %v) recorded %d units, want recorded = %v", offset, lost, len(out.states), want)
	}
}

func TestCollect_RepeatsAfterAnHour(t *testing.T) {
	newOne := func(t *testing.T) *rig {
		r := newRig(t, "mariadb.service")
		r.fake.add(running("mariadb.service"))
		return r
	}
	t.Run("an unchanged unit", func(t *testing.T) {
		r := newOne(t)
		wantRecorded(t, r, 0, false, true)
		wantRecorded(t, r, 30*time.Minute, false, false)
		wantRecorded(t, r, 59*time.Minute+59*time.Second, false, false)
		wantRecorded(t, r, time.Hour, false, true)
		wantRecorded(t, r, 2*time.Hour-time.Second, false, false)
		wantRecorded(t, r, 2*time.Hour, false, true)
	})
	t.Run("the hour counts from the last record, a change included", func(t *testing.T) {
		r := newOne(t)
		wantRecorded(t, r, 0, false, true)
		r.fake.change("mariadb.service", func(u *Unit) { u.Restarts++ })
		wantRecorded(t, r, 30*time.Minute, false, true)
		wantRecorded(t, r, 90*time.Minute-time.Second, false, false)
		wantRecorded(t, r, 90*time.Minute, false, true)
	})
}

// missingUnitsEntries are the tracked entries of the missing-unit tests: two exact entries systemd has no file for, a pattern
// that matches nothing, a masked and a broken unit.
var missingUnitsEntries = []string{"z.service", "mariadb.service", "apache2.service", "plesk*.service", "masked.service", "broken.service"}

func populateMissingUnits(f *fakeSystemd) {
	f.add(running("mariadb.service"))
	f.add(Unit{ID: "masked.service", LoadState: "masked", ActiveState: "inactive", SubState: "dead"})
	f.add(Unit{ID: "broken.service", LoadState: "bad-setting", ActiveState: "inactive", SubState: "dead"})
}

// wantLogLines checks the number of lines logged so far.
func wantLogLines(t *testing.T, logs *logStore, want int, what string) {
	t.Helper()

	if n := len(logs.all()); n != want {
		t.Errorf("log holds %d lines %s, want %d; log:\n%s", n, what, want, logs.text())
	}
}

func TestCollect_WarnsOnceAboutMissingUnits(t *testing.T) {
	tests := []struct {
		name string
		fn   func(*testing.T)
	}{
		{"one line in file order, not repeated", warnsOnceInFileOrder},
		{"a restart of the agent warns again", warnsAgainAfterRestart},
		{"a failed first run comes first, the warning with the first success", warnsAtFirstSuccess},
		{"a unit installed or removed later logs nothing", logsNothingForLaterChanges},
		{"nothing missing logs nothing", logsNothingWithoutMissingUnits},
	}
	for _, tc := range tests {
		t.Run(tc.name, tc.fn)
	}
}

func warnsOnceInFileOrder(t *testing.T) {
	r := newRig(t, missingUnitsEntries...)
	populateMissingUnits(r.fake)

	first := r.collect(runAt(0, false))
	r.collect(runAt(30*time.Second, false))
	r.collect(runAt(60*time.Second, true))
	r.collect(runAt(90*time.Second, false))

	first.wantUnits(t, "first run", "z.service", "mariadb.service", "apache2.service", "masked.service", "broken.service")
	for _, name := range []string{"z.service", "apache2.service"} {
		if s := first.states[name]; s == nil || s.LoadState != "not-found" {
			t.Errorf("record of %s = %+v, want load_state not-found", name, s)
		}
	}
	warns := wantLogged(t, r.logs, slog.LevelWarn, "systemd units not found", 1)
	if len(warns) == 1 && (warns[0].attrs["count"] != "2" || warns[0].attrs["units"] != "z.service,apache2.service") {
		t.Errorf("warning count = %q, units = %q, want %q and %q", warns[0].attrs["count"], warns[0].attrs["units"], "2", "z.service,apache2.service")
	}
	wantLogLines(t, r.logs, 1, "(only the warning)")
}

func warnsAgainAfterRestart(t *testing.T) {
	r := newRig(t, missingUnitsEntries...)
	populateMissingUnits(r.fake)
	r.collect(runAt(0, false))
	again := &logStore{}
	restarted := New(Sources{Systemd: r.fake}, missingUnitsEntries, testSchedule, slog.New(&recordingHandler{store: again}))

	recs, err := restarted.Collect(context.Background(), runAt(time.Minute, false))

	if err != nil || len(recs) != 5 {
		t.Fatalf("Collect() = %d records, %v, want 5 records (every tracked unit) and no error", len(recs), err)
	}
	wantLogged(t, again, slog.LevelWarn, "systemd units not found", 1)
}

func warnsAtFirstSuccess(t *testing.T) {
	r := newRig(t, missingUnitsEntries...)
	populateMissingUnits(r.fake)
	r.fake.failNext(stageRead, errBus)
	if _, err := r.c.Collect(context.Background(), runAt(0, false)); err == nil {
		t.Fatal("Collect() error = nil, want the failure of the read")
	}
	wantLogLines(t, r.logs, 0, "after a failed run")

	out := r.collect(runAt(30*time.Second, true))

	if len(out.states) != 5 {
		t.Errorf("first successful run recorded %d units, want 5", len(out.states))
	}
	wantLogged(t, r.logs, slog.LevelWarn, "systemd units not found", 1)
}

func logsNothingForLaterChanges(t *testing.T) {
	r := newRig(t, missingUnitsEntries...)
	populateMissingUnits(r.fake)
	r.collect(runAt(0, false))
	r.fake.add(running("apache2.service"))
	r.fake.unload("mariadb.service")

	out := r.collect(runAt(30*time.Second, false))

	out.wantUnits(t, "run after the changes", "apache2.service", "mariadb.service")
	if s := out.states["mariadb.service"]; s == nil || s.LoadState != "not-found" {
		t.Errorf("record of the removed unit = %+v, want load_state not-found", s)
	}
	wantLogged(t, r.logs, slog.LevelWarn, "systemd units not found", 1)
	wantLogLines(t, r.logs, 1, "(only the warning of the start)")
}

func logsNothingWithoutMissingUnits(t *testing.T) {
	r := newRig(t, "mariadb.service", "plesk*.service")
	r.fake.add(running("mariadb.service"))
	r.collect(runAt(0, false))
	r.fake.change("mariadb.service", func(u *Unit) { u.Restarts++ })
	r.collect(runAt(30*time.Second, false))

	wantLogLines(t, r.logs, 0, "(no line per run or change)")
}

func TestCollect_PatternsTrackLoadedMatches(t *testing.T) {
	r := newRig(t, "nginx.service", "plesk-php*-fpm.service")
	r.fake.add(running("nginx.service"))
	r.fake.add(running("plesk-php82-fpm.service"))
	r.fake.add(running("plesk-php83-fpm.service"))
	r.fake.add(missing("plesk-php99-fpm.service")) // a stub any local user can make systemd load

	first := r.collect(runAt(0, false))

	first.wantUnits(t, "first run", "nginx.service", "plesk-php82-fpm.service", "plesk-php83-fpm.service")
	wantReads := [][]string{{"nginx.service", "plesk-php82-fpm.service", "plesk-php83-fpm.service"}}
	if got := r.fake.readNames(); !slices.EqualFunc(got, wantReads, slices.Equal) {
		t.Errorf("Read calls = %v, want %v (exact entries first, then the matches in name order, no not-found match)", got, wantReads)
	}
	if got := r.fake.listed(); !slices.EqualFunc(got, [][]string{{"plesk-php*-fpm.service"}}, slices.Equal) {
		t.Errorf("List calls = %v, want one with the pattern entries only", got)
	}

	r.fake.add(running("plesk-php84-fpm.service"))
	r.collect(runAt(30*time.Second, false)).wantUnits(t, "run that sees a new match", "plesk-php84-fpm.service")

	r.fake.change("plesk-php99-fpm.service", func(u *Unit) { *u = running(u.ID) })
	r.collect(runAt(60*time.Second, false)).wantUnits(t, "run that sees the stub loaded", "plesk-php99-fpm.service")

	r.fake.unload("plesk-php83-fpm.service")
	out := r.collect(runAt(90*time.Second, false))
	out.wantUnits(t, "run after systemd unloaded a match", "plesk-php83-fpm.service")
	if s := out.states["plesk-php83-fpm.service"]; s == nil || s.LoadState != "not-found" {
		t.Errorf("record of the unloaded match = %+v, want load_state not-found (still read by name)", s)
	}
	r.collect(runAt(120*time.Second, false)).wantUnits(t, "run after the unload was recorded")

	if n := len(r.logs.all()); n != 0 {
		t.Errorf("log holds %d lines, want none (patterns and unloaded matches are no missing units); log:\n%s", n, r.logs.text())
	}
}

// addNumbered makes systemd list the running units svcNNN.service for the numbers from..to-1.
func addNumbered(f *fakeSystemd, from, to int) {
	for i := from; i < to; i++ {
		f.add(running(fmt.Sprintf("svc%03d.service", i)))
	}
}

func TestCollect_CapsTrackedUnits(t *testing.T) {
	t.Run("exact entries first, the matches in name order, the rest cut once", capsAtTheLimitAndKeepsTracked)
	t.Run("the warning comes at the first run that leaves a match out", capsWarnsAtFirstCut)
}

func capsAtTheLimitAndKeepsTracked(t *testing.T) {
	r := newRig(t, "a.service", "b.service", "c.service", "svc*.service")
	for _, name := range []string{"a.service", "b.service", "c.service"} {
		r.fake.add(running(name))
	}
	addNumbered(r.fake, 0, 300)

	first := r.collect(runAt(0, false))

	wantNames := []string{"a.service", "b.service", "c.service"}
	for i := range MaxTracked - 3 {
		wantNames = append(wantNames, fmt.Sprintf("svc%03d.service", i))
	}
	if len(first.states) != MaxTracked || MaxTracked != 256 {
		t.Fatalf("first run recorded %d units (cap %d), want 256", len(first.states), MaxTracked)
	}
	if got := r.fake.readNames(); len(got) != 1 || !slices.Equal(got[0], wantNames) {
		t.Errorf("Read names = %v, want the exact entries and the 253 first matches in name order", got)
	}
	wantLeftOut(t, r.logs, "47")

	r.fake.add(running("svc-first.service")) // sorts before every tracked match
	r.fake.unload("svc010.service")          // a tracked match that systemd unloaded stays tracked
	out := r.collect(runAt(30*time.Second, false))

	out.wantUnits(t, "run with a new match beyond the cap", "svc010.service")
	if got := r.fake.readNames(); len(got) != 2 || !slices.Equal(got[1], wantNames) {
		t.Errorf("Read names of the second run = %v, want the same 256 (no tracked unit dropped, none added)", got)
	}
	wantLogged(t, r.logs, slog.LevelWarn, "systemd units cut", 1)
}

// wantLeftOut checks that the cut warning was logged once with the count of left out matches.
func wantLeftOut(t *testing.T, logs *logStore, want string) {
	t.Helper()

	cut := wantLogged(t, logs, slog.LevelWarn, "systemd units cut", 1)
	if len(cut) == 1 && cut[0].attrs["left_out"] != want {
		t.Errorf("left_out = %q, want %q", cut[0].attrs["left_out"], want)
	}
}

func capsWarnsAtFirstCut(t *testing.T) {
	r := newRig(t, "svc*.service")
	addNumbered(r.fake, 0, 250)
	if out := r.collect(runAt(0, false)); len(out.states) != 250 {
		t.Fatalf("first run recorded %d units, want 250", len(out.states))
	}
	wantLogged(t, r.logs, slog.LevelWarn, "systemd units cut", 0)

	addNumbered(r.fake, 250, 260)
	out := r.collect(runAt(30*time.Second, false))

	if len(out.states) != 6 {
		t.Errorf("second run recorded %d units, want the 6 matches that fit", len(out.states))
	}
	wantLeftOut(t, r.logs, "4")
	addNumbered(r.fake, 260, 270)
	r.collect(runAt(60*time.Second, false))
	wantLogged(t, r.logs, slog.LevelWarn, "systemd units cut", 1)
}

func TestCollect_SkipsMatchesTheWireRefuses(t *testing.T) {
	r := newRig(t, "zq*.service")
	r.fake.add(running("zq1-ok.service"))
	r.fake.add(running(`zq7\x2dleak.service`))
	r.fake.add(running(`zq7\x2dmore.service`))

	out := r.collect(runAt(0, false))

	out.wantUnits(t, "first run", "zq1-ok.service")
	warns := wantLogged(t, r.logs, slog.LevelWarn, "systemd units not tracked", 1)
	if len(warns) == 1 && warns[0].attrs["count"] != "2" {
		t.Errorf("warning count = %q, want %q", warns[0].attrs["count"], "2")
	}
	r.fake.add(running(`zq8\x2dlater.service`))
	r.collect(runAt(30*time.Second, false)).wantUnits(t, "second run")
	wantLogged(t, r.logs, slog.LevelWarn, "systemd units not tracked", 1)
	for _, leak := range []string{"zq7", "zq8", "x2d", `\`} {
		if strings.Contains(r.logs.text(), leak) {
			t.Errorf("log holds %q, want no part of a refused name; log:\n%s", leak, r.logs.text())
		}
	}
	for _, names := range r.fake.readNames() {
		if !slices.Equal(names, []string{"zq1-ok.service"}) {
			t.Errorf("Read names = %v, want only the accepted match", names)
		}
	}
}

func TestCollect_BusFailureFailsTheRun(t *testing.T) {
	for _, stage := range []string{stageOpen, stageList, stageRead} {
		t.Run(stage, func(t *testing.T) {
			r := newRig(t, "mariadb.service", "plesk*.service")
			r.fake.add(running("mariadb.service"))
			r.fake.failNext(stage, errBus)

			recs, err := r.c.Collect(context.Background(), runAt(0, false))

			if !errors.Is(err, errBus) || len(recs) != 0 {
				t.Errorf("Collect() = %d records, %v, want no record and an error wrapping the bus error", len(recs), err)
			}
			opens, closes := r.fake.counts()
			wantCloses := 1
			if stage == stageOpen {
				wantCloses = 0
			}
			if opens != 1 || closes != wantCloses {
				t.Errorf("reader opened %d times and closed %d times, want 1 and %d", opens, closes, wantCloses)
			}
			if n := len(r.logs.all()); n != 0 {
				t.Errorf("a failed run logged %d lines, want none; log:\n%s", n, r.logs.text())
			}

			out := r.collect(runAt(30*time.Second, true))

			out.wantUnits(t, "run after the bus returned", "mariadb.service")
		})
	}
}

func TestCollect_HungBusEndsAtTheDeadline(t *testing.T) {
	for _, stage := range []string{stageOpen, stageList, stageRead} {
		t.Run(stage, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				r := newRig(t, "mariadb.service", "plesk*.service")
				r.fake.add(running("mariadb.service"))
				r.fake.hangNext(stage)
				ctx, cancel := context.WithTimeout(context.Background(), testSchedule.Timeout)
				defer cancel()
				type result struct {
					recs []model.Record
					err  error
				}
				done := make(chan result, 1)
				go func() {
					recs, err := r.c.Collect(ctx, runAt(0, false))
					done <- result{recs, err}
				}()

				got := <-done

				if !errors.Is(got.err, context.DeadlineExceeded) || len(got.recs) != 0 {
					t.Errorf("Collect() = %d records, %v, want no record and context.DeadlineExceeded", len(got.recs), got.err)
				}
				if opens, closes := r.fake.counts(); closes != opens-btoi(stage == stageOpen) {
					t.Errorf("reader opened %d times and closed %d times, want every opened reader closed", opens, closes)
				}
				r.collect(runAt(30*time.Second, true)).wantUnits(t, "run after the hang", "mariadb.service")
			})
		})
	}
}

func btoi(b bool) int {
	if b {
		return 1
	}
	return 0
}

func TestCollect_PreviousLostRecordsEveryUnit(t *testing.T) {
	r := newRig(t, "mariadb.service", "nginx.service", "apache2.service")
	r.fake.add(running("mariadb.service"))
	r.fake.add(running("nginx.service"))
	r.fake.add(missing("apache2.service"))
	all := []string{"mariadb.service", "nginx.service", "apache2.service"}
	r.collect(runAt(0, false)).wantUnits(t, "first run", all...)
	r.collect(runAt(30*time.Second, false)).wantUnits(t, "run without a change")
	r.fake.change("nginx.service", func(u *Unit) { u.Restarts = 4 })

	lost := r.collect(runAt(time.Minute, true))

	lost.wantUnits(t, "run after a lost sample", all...)
	if s := lost.states["nginx.service"]; s == nil || s.Restarts != 4 {
		t.Errorf("record of the changed unit = %+v, want its current state (restarts 4)", s)
	}
	r.collect(runAt(90*time.Second, false)).wantUnits(t, "run after the full record")
	wantLogged(t, r.logs, slog.LevelWarn, "systemd units not found", 1)
	if n := len(r.logs.all()); n != 1 {
		t.Errorf("log holds %d lines, want only the warning of the start; log:\n%s", n, r.logs.text())
	}
	r.collect(runAt(time.Minute+time.Hour-time.Second, false)).wantUnits(t, "run just under an hour after the lost-sample record")
	r.collect(runAt(time.Minute+time.Hour, false)).wantUnits(t, "run an hour after the lost-sample record", all...)
}

// memSink is a scheduler sink that keeps what it is given and can refuse the first batch.
type memSink struct {
	mu        sync.Mutex
	batches   [][]model.Record
	failFirst bool
}

// Put implements scheduler.Putter.
func (s *memSink) Put(_ context.Context, records []model.Record) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	n := len(s.batches)
	s.batches = append(s.batches, slices.Clone(records))
	if s.failFirst && n == 0 {
		return errors.New("sink is full")
	}
	return nil
}

func (s *memSink) all() [][]model.Record {
	s.mu.Lock()
	defer s.mu.Unlock()

	return slices.Clone(s.batches)
}

// runUnderScheduler runs the collector as the agent does for d of fake time and returns the sink and the scheduler's log.
func runUnderScheduler(t *testing.T, r *rig, sink *memSink, d time.Duration) *logStore {
	t.Helper()

	logs := &logStore{}
	sched, err := scheduler.New([]scheduler.Job{{Collector: r.c, Schedule: testSchedule}}, sink, slog.New(&recordingHandler{store: logs}))
	if err != nil {
		t.Fatalf("scheduler.New() error = %v, want nil", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		sched.Run(ctx)
	}()
	time.Sleep(d)
	synctest.Wait()
	cancel()
	<-done
	return logs
}

// statesByStart returns the units of the service states per run, keyed by the offset of their capture time from base.
func statesByStart(sink *memSink, base time.Time) map[time.Duration][]string {
	out := map[time.Duration][]string{}
	for _, batch := range sink.all() {
		for _, rec := range batch {
			state, ok := rec.Data.(*model.ServiceState)
			if !ok {
				continue
			}
			at := rec.CapturedAt.Sub(base)
			out[at] = append(out[at], state.Unit)
		}
	}
	for _, names := range out {
		slices.Sort(names)
	}
	return out
}

func TestCollect_LostSampleUnderScheduler(t *testing.T) {
	both := []string{"a.service", "b.service"}
	tests := []struct {
		name  string
		arm   func(*fakeSystemd, *memSink)
		first []string // units recorded by the run at 0 s that the sink kept
	}{
		{"a timeout", func(f *fakeSystemd, _ *memSink) { f.hangNext(stageRead) }, nil},
		{"an error", func(f *fakeSystemd, _ *memSink) { f.failNext(stageList, errBus) }, nil},
		{"a refused batch", func(_ *fakeSystemd, s *memSink) { s.failFirst = true }, both},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				r := newRig(t, "a.service", "b.service", "plesk*.service")
				r.fake.add(running("a.service"))
				r.fake.add(running("b.service"))
				sink := &memSink{}
				tc.arm(r.fake, sink)

				base := time.Now()
				runUnderScheduler(t, r, sink, 70*time.Second)

				want := map[time.Duration][]string{30 * time.Second: both}
				if tc.first != nil {
					want[0] = tc.first
				}
				if got := statesByStart(sink, base); !maps.EqualFunc(got, want, slices.Equal) {
					t.Errorf("units recorded per run = %v, want %v (the run after the loss records every unit, the run after that none)", got, want)
				}
			})
		})
	}
}

// refusals are the states of a unit the model refuses, with the field the collector names.
var refusals = []struct {
	name  string
	field string
	value string
	set   func(*Unit, string)
}{
	{"active_state", "active_state", "frozen", func(u *Unit, v string) { u.ActiveState = v }},
	{"sub_state with a capital", "sub_state", "Running", func(u *Unit, v string) { u.SubState = v }},
	{"sub_state of 65 bytes", "sub_state", strings.Repeat("a", 65), func(u *Unit, v string) { u.SubState = v }},
	{"load_state", "load_state", "unknown-state", func(u *Unit, v string) { u.LoadState = v }},
}

func TestCollect_RefusedUnitBecomesItsOwnGap(t *testing.T) {
	for _, tc := range refusals {
		t.Run(tc.name, func(t *testing.T) { refusedUnitCase(t, tc.field, tc.value, tc.set) })
	}
}

// refusedUnitCase makes bad.service carry value in a field the model refuses and follows it through its gaps and its recovery.
func refusedUnitCase(t *testing.T, field, value string, set func(*Unit, string)) {
	r := newRig(t, "ok.service", "bad.service")
	r.fake.add(running("ok.service"))
	r.fake.add(running("bad.service"))
	r.collect(runAt(0, false)).wantUnits(t, "first run", "ok.service", "bad.service")

	// A run that leaves the unit out and keeps the other record.
	r.fake.change("bad.service", func(u *Unit) { set(u, value) })
	r.fake.change("ok.service", func(u *Unit) { u.Restarts++ })
	wantOwnGap(t, r.collect(runAt(30*time.Second, false)), 30*time.Second, 30*time.Second, "ok.service")
	warns := wantLogged(t, r.logs, slog.LevelWarn, "systemd unit state not recorded", 1)
	if len(warns) == 1 && (warns[0].attrs["unit"] != "bad.service" || warns[0].attrs["field"] != field) {
		t.Errorf("warning unit = %q, field = %q, want %q and %q", warns[0].attrs["unit"], warns[0].attrs["field"], "bad.service", field)
	}

	// Each further such run is a gap again and logs nothing new; the interval of the gap follows the run's mode.
	r.fake.change("ok.service", func(u *Unit) { u.Restarts++ })
	wantOwnGap(t, r.collect(runAt(time.Minute, false)), time.Minute, 30*time.Second, "ok.service")
	pressed := scheduler.Run{Start: epoch.Add(90 * time.Second), Pressure: true}
	wantOwnGap(t, mustCollect(t, r, pressed), 90*time.Second, 10*time.Second)
	wantLogged(t, r.logs, slog.LevelWarn, "systemd unit state not recorded", 1)
	wantLogged(t, r.logs, slog.LevelInfo, "systemd unit state recorded again", 0)

	// The unit is accepted again: recorded, with the line of recovery, and no gap.
	r.fake.change("bad.service", func(u *Unit) { *u = running("bad.service") })
	r.collect(runAt(2*time.Minute, false)).wantUnits(t, "run that accepts the unit again", "bad.service")
	recovered := wantLogged(t, r.logs, slog.LevelInfo, "systemd unit state recorded again", 1)
	if len(recovered) == 1 && recovered[0].attrs["unit"] != "bad.service" {
		t.Errorf("recovery unit = %q, want %q", recovered[0].attrs["unit"], "bad.service")
	}
	r.collect(runAt(150*time.Second, false)).wantUnits(t, "run after the recovery")
	wantLogged(t, r.logs, slog.LevelInfo, "systemd unit state recorded again", 1)

	text := r.logs.text()
	for _, leak := range []string{value, "model:", "invalid"} {
		if strings.Contains(text, leak) {
			t.Errorf("log holds %q, want neither the refused value nor the validation text; log:\n%s", leak, text)
		}
	}
}

// mustCollect runs the collector for run and sorts its records.
func mustCollect(t *testing.T, r *rig, run scheduler.Run) outcome {
	t.Helper()

	recs, err := r.c.Collect(context.Background(), run)
	if err != nil {
		t.Fatalf("Collect() error = %v, want nil", err)
	}
	return sortRecords(t, run, recs)
}

// wantOwnGap checks that the run kept exactly the records of kept and returned one gap of its own for the run's interval.
func wantOwnGap(t *testing.T, out outcome, offset, interval time.Duration, kept ...string) {
	t.Helper()

	if got := out.units(); !slices.Equal(got, kept) {
		t.Errorf("run at %v kept the records of %v, want %v", offset, got, kept)
	}
	if len(out.gaps) != 1 {
		t.Fatalf("run at %v returned %d gaps, want 1", offset, len(out.gaps))
	}
	g := out.gaps[0]
	from := epoch.Add(offset)
	if !g.From.Equal(from) || !g.To.Equal(from.Add(interval)) || g.Cause != model.GapUnknown || g.Collector != Name {
		t.Errorf("gap = %+v, want from %v to %v, cause unknown, collector %q", g, from, from.Add(interval), Name)
	}
}

// countKinds returns how many service states and gaps the sink holds, and the first gap.
func countKinds(sink *memSink) (states, gaps int, gap *model.Gap) {
	for _, batch := range sink.all() {
		for _, rec := range batch {
			switch data := rec.Data.(type) {
			case *model.ServiceState:
				states++
			case *model.Gap:
				gaps++
				gap = data
			}
		}
	}
	return states, gaps, gap
}

func TestCollect_RefusedUnitIsNotRefusedByTheScheduler(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, "ok.service", "bad.service")
		r.fake.add(running("ok.service"))
		bad := running("bad.service")
		bad.ActiveState = "frozen"
		r.fake.add(bad)
		sink := &memSink{}

		logs := runUnderScheduler(t, r, sink, 20*time.Second)

		states, gaps, gap := countKinds(sink)
		if states != 1 || gaps != 1 {
			t.Fatalf("sink holds %d service states and %d gaps, want 1 and 1 (the accepted unit and the gap of the collector)", states, gaps)
		}
		if gap.Collector != Name || gap.Cause != model.GapUnknown {
			t.Errorf("gap = %+v, want cause unknown and collector %q", gap, Name)
		}
		if refused := logs.find("record refused"); len(refused) != 0 {
			t.Errorf("scheduler logged %d lines %q, want none: the collector checks its records itself", len(refused), "record refused")
		}
	})
}

func TestBusSystemd_OpenReportsAnUnreachableBus(t *testing.T) {
	socket := filepath.Join(t.TempDir(), "no-such-bus.sock")

	reader, err := BusSystemd(socket).Open(t.Context())

	if err == nil || reader != nil {
		t.Errorf("BusSystemd(%q).Open() = %v, %v, want an error and a nil Reader", socket, reader, err)
	}
}

// allowedImports are the packages the non-test files of internal/svcstats import (AC11): no program, file, socket, cgo or unsafe.
var allowedImports = []string{
	"cmp", "context", "errors", "fmt", "log/slog", "maps", "math", "path", "slices", "sort", "strconv", "strings", "sync", "time",
	"unicode/utf8",
	"github.com/LarsLaskowski/Vandox/internal/model", "github.com/LarsLaskowski/Vandox/internal/scheduler",
	"github.com/LarsLaskowski/Vandox/internal/sdbus", "github.com/LarsLaskowski/Vandox/internal/wire",
}

// sdbusPackage is the one package whose selectors are restricted: the collector dials and holds sessions, and reads only through Reader.
const sdbusPackage = "github.com/LarsLaskowski/Vandox/internal/sdbus"

// allowedSelectors maps a package to the only selectors of it the non-test files may use.
var allowedSelectors = map[string][]string{
	sdbusPackage: {"Dial", "SystemBusPath", "Session", "Unit"},
}

// restrictedNames returns the local name of every import of file whose path is a key of allowedSelectors, and the violations for
// dot imports of such a package; a blank import binds no name and is fine.
func restrictedNames(name string, file *ast.File) (names map[string]string, violations []string) {
	names = map[string]string{}
	for _, imp := range file.Imports {
		pkg, err := strconv.Unquote(imp.Path.Value)
		if _, restricted := allowedSelectors[pkg]; err != nil || !restricted {
			continue
		}
		local := pkg[strings.LastIndex(pkg, "/")+1:]
		if imp.Name != nil {
			local = imp.Name.Name
		}
		switch local {
		case "_":
		case ".":
			violations = append(violations, name+": dot import of "+pkg+" is not allowed")
		default:
			names[local] = pkg
		}
	}
	return names, violations
}

// disallowedSelectors returns the uses in file of selectors of a restricted package, under whatever local name it is imported, that
// allowedSelectors does not list, and the dot imports of a restricted package.
func disallowedSelectors(name string, file *ast.File) []string {
	names, violations := restrictedNames(name, file)
	ast.Inspect(file, func(n ast.Node) bool {
		sel, ok := n.(*ast.SelectorExpr)
		if !ok {
			return true
		}
		id, ok := sel.X.(*ast.Ident)
		if !ok {
			return true
		}
		pkg, bound := names[id.Name]
		if bound && id.Obj == nil && !slices.Contains(allowedSelectors[pkg], sel.Sel.Name) {
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
		pkg, err := strconv.Unquote(imp.Path.Value)
		if err != nil || !slices.Contains(allowedImports, pkg) {
			violations = append(violations, name+": imports "+imp.Path.Value+", want only "+strings.Join(allowedImports, " "))
		}
	}
	return violations
}

func TestSvcstats_ImportsOnlyAllowedPackages(t *testing.T) {
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
	if checked < 1 {
		t.Errorf("checked %d production files, want the collector's files", checked)
	}
}
