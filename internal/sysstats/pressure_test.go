package sysstats_test

import (
	"io/fs"
	"log/slog"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

const (
	msgPressureUnavailable = "pressure stall information unavailable"
	msgPressureAvailable   = "pressure stall information available"
)

var pressureResources = []string{"memory", "cpu", "io"}

// dropPressure returns list without the pressure records of the resources.
func dropPressure(list []wantRec, resources ...string) []wantRec {
	return drop(list, func(w wantRec) bool {
		return strings.HasPrefix(w.name, "pressure_") && slices.Contains(resources, w.labels["resource"])
	})
}

// warnUnavailable is the warning that names the resources.
func warnUnavailable(resources string) wantLog {
	return wantLog{slog.LevelWarn, msgPressureUnavailable, map[string]string{"collector": "system", "resources": resources}}
}

// unavailableCase is a way the pressure files are not offered.
type unavailableCase struct {
	name      string
	sources   func(tr *tree) sysstats.Sources
	resources []string
}

func unavailableCases() []unavailableCase {
	return []unavailableCase{
		{"pressure directory missing", func(tr *tree) sysstats.Sources {
			for _, r := range pressureResources {
				delete(tr.proc, "pressure/"+r)
			}
			return tr.sources()
		}, pressureResources},
		{"only io missing", func(tr *tree) sysstats.Sources {
			delete(tr.proc, "pressure/io")
			return tr.sources()
		}, []string{"io"}},
		{"open of memory answers operation not supported", func(tr *tree) sysstats.Sources {
			src := tr.sources()
			src.Proc = failFS{FS: tr.proc, openErr: map[string]error{"pressure/memory": eopnotsupp}}
			return src
		}, []string{"memory"}},
		{"read of memory answers operation not supported", func(tr *tree) sysstats.Sources {
			src := tr.sources()
			src.Proc = failFS{FS: tr.proc, readErr: map[string]error{"pressure/memory": eopnotsupp}}
			return src
		}, []string{"memory"}},
	}
}

func TestSystem_PressureUnavailable(t *testing.T) {
	for _, tt := range unavailableCases() {
		t.Run(tt.name, func(t *testing.T) {
			tr := loadTree(t)
			store, log := newLogStore()
			c := sysstats.NewSystem(tt.sources(tr), log)

			first := mustCollect(t, c, runStart)
			requireRecords(t, runStart, first, dropPressure(wantSystem(t, baseSpec()), tt.resources...))
			for i := 1; i <= 2; i++ {
				got := mustCollect(t, c, runStart.Add(time.Duration(i)*30*time.Second))
				requireNoPressureOf(t, got, tt.resources)
			}

			requireLogged(t, store, "after three samples", []wantLog{warnUnavailable(strings.Join(tt.resources, ","))})
		})
	}

	t.Run("a permission error fails the sample", func(t *testing.T) {
		tr := loadTree(t)
		store, log := newLogStore()
		src := tr.sources()
		src.Proc = failFS{FS: tr.proc, openErr: map[string]error{"pressure/memory": fs.ErrPermission}}

		got, err := collect(sysstats.NewSystem(src, log), runStart)

		if err == nil || !strings.Contains(err.Error(), "pressure/memory") {
			t.Errorf("Collect() error = %v, want an error naming pressure/memory", err)
		}
		if len(got) != 0 {
			t.Errorf("Collect() returned %d records with its error, want none", len(got))
		}
		requireLogged(t, store, "after the failed sample", nil)
	})
}

// requireNoPressureOf checks that got holds no pressure record of the resources.
func requireNoPressureOf(t *testing.T, got []model.Record, resources []string) {
	t.Helper()

	for _, r := range find(got, "pressure_avg10") {
		if slices.Contains(resources, r.labels["resource"]) {
			t.Errorf("sample holds the pressure record %s of an unavailable resource", r)
		}
	}
}

func TestSystem_PressureAvailabilityLoggedOnChange(t *testing.T) {
	orig := loadTree(t)
	steps := []struct {
		name        string
		unavailable []string
		logged      []wantLog // everything logged up to and including this sample
	}{
		{"all available at the start", nil, nil},
		{"io goes away", []string{"io"}, []wantLog{warnUnavailable("io")}},
		{"io stays away", []string{"io"}, []wantLog{warnUnavailable("io")}},
		{"memory goes away too", []string{"memory", "io"}, []wantLog{warnUnavailable("io"), warnUnavailable("memory,io")}},
		{"memory and io stay away", []string{"memory", "io"}, []wantLog{warnUnavailable("io"), warnUnavailable("memory,io")}},
		{
			"everything is back", nil,
			[]wantLog{warnUnavailable("io"), warnUnavailable("memory,io"), {slog.LevelInfo, msgPressureAvailable, nil}},
		},
		{
			"everything stays", nil,
			[]wantLog{warnUnavailable("io"), warnUnavailable("memory,io"), {slog.LevelInfo, msgPressureAvailable, nil}},
		},
		{
			"cpu goes away", []string{"cpu"},
			[]wantLog{
				warnUnavailable("io"), warnUnavailable("memory,io"), {slog.LevelInfo, msgPressureAvailable, nil},
				warnUnavailable("cpu"),
			},
		},
	}

	tr := loadTree(t)
	store, log := newLogStore()
	c := sysstats.NewSystem(tr.sources(), log)
	for i, step := range steps {
		for _, r := range pressureResources {
			name := "pressure/" + r
			if slices.Contains(step.unavailable, r) {
				delete(tr.proc, name)
			} else {
				set(tr.proc, name, text(t, orig.proc, name))
			}
		}

		mustCollect(t, c, runStart.Add(time.Duration(i)*30*time.Second))

		requireLogged(t, store, "sample "+step.name, step.logged)
	}

	t.Run("a failed sample logs nothing", func(t *testing.T) {
		tr := loadTree(t)
		store, log := newLogStore()
		c := sysstats.NewSystem(tr.sources(), log)
		delete(tr.proc, "pressure/io")
		valid := text(t, tr.proc, "meminfo")
		set(tr.proc, "meminfo", valid+"broken\n")

		if _, err := collect(c, runStart); err == nil {
			t.Fatalf("Collect() with a broken meminfo error = nil, want an error")
		}
		requireLogged(t, store, "after the failed sample", nil)

		set(tr.proc, "meminfo", valid)
		mustCollect(t, c, runStart.Add(30*time.Second))
		requireLogged(t, store, "after the good sample", []wantLog{warnUnavailable("io")})
	})
}

func TestSystem_PressureWithoutFullLine(t *testing.T) {
	tr := loadTree(t)
	store, log := newLogStore()
	set(tr.proc, "pressure/cpu", "some avg10=0.12 avg60=0.93 avg300=0.39 total=41802781\n")

	got := mustCollect(t, sysstats.NewSystem(tr.sources(), log), runStart)

	want := drop(wantSystem(t, baseSpec()), func(w wantRec) bool {
		return strings.HasPrefix(w.name, "pressure_") && w.labels["resource"] == "cpu" && w.labels["kind"] == "full"
	})
	requireRecords(t, runStart, got, want)
	requireLogged(t, store, "after the sample", nil)
}
