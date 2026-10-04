package model_test

import (
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validServiceState() *model.ServiceState {
	return &model.ServiceState{
		Unit:          "nginx.service",
		LoadState:     "loaded",
		ActiveState:   "active",
		SubState:      "running",
		ActiveEnterAt: testTime,
		Restarts:      2,
	}
}

func TestServiceState_Kind(t *testing.T) {
	if got := validServiceState().Kind(); got != model.KindServiceState {
		t.Errorf("Kind() = %q, want %q", got, model.KindServiceState)
	}
}

func TestServiceState_Validate_Valid(t *testing.T) {
	tests := []struct {
		name string
		p    *model.ServiceState
	}{
		{"fully populated", validServiceState()},
		{"minimal", &model.ServiceState{Unit: "a", LoadState: "loaded", ActiveState: "inactive"}},
		{"unit of 256 bytes", &model.ServiceState{Unit: strings.Repeat("a", 256), LoadState: "loaded", ActiveState: "active"}},
		{"unit with allowed punctuation", &model.ServiceState{Unit: "user@1000.service:x_y-z", LoadState: "loaded", ActiveState: "active"}},
		{"sub_state of 64 bytes", &model.ServiceState{Unit: "a", LoadState: "loaded", ActiveState: "active", SubState: strings.Repeat("a", 64)}},
		{"sub_state with dash and digits", &model.ServiceState{Unit: "a", LoadState: "loaded", ActiveState: "active", SubState: "start-pre2"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}

	for _, v := range []string{"loaded", "not-found", "bad-setting", "error", "masked", "stub", "merged"} {
		t.Run("load_state "+v, func(t *testing.T) {
			p := validServiceState()
			p.LoadState = v
			requireValid(t, p)
		})
	}
	for _, v := range []string{"active", "reloading", "inactive", "failed", "activating", "deactivating", "maintenance", "refreshing"} {
		t.Run("active_state "+v, func(t *testing.T) {
			p := validServiceState()
			p.ActiveState = v
			requireValid(t, p)
		})
	}
}

func TestServiceState_Validate_Invalid(t *testing.T) {
	cases := []invalidCase[*model.ServiceState]{
		{"unit empty", func(p *model.ServiceState) { p.Unit = "" }, "unit"},
		{"unit with space", func(p *model.ServiceState) { p.Unit = "a b" }, "unit"},
		{"unit with trailing newline", func(p *model.ServiceState) { p.Unit = "a\n" }, "unit"},
		{"unit too long", func(p *model.ServiceState) { p.Unit = strings.Repeat("a", 257) }, "unit"},
		{"load_state empty", func(p *model.ServiceState) { p.LoadState = "" }, "load_state"},
		{"load_state unknown", func(p *model.ServiceState) { p.LoadState = "bogus" }, "load_state"},
		{"load_state upper case", func(p *model.ServiceState) { p.LoadState = "Loaded" }, "load_state"},
		{"active_state empty", func(p *model.ServiceState) { p.ActiveState = "" }, "active_state"},
		{"active_state unknown", func(p *model.ServiceState) { p.ActiveState = "bogus" }, "active_state"},
		{"sub_state upper case", func(p *model.ServiceState) { p.SubState = "Running" }, "sub_state"},
		{"sub_state with space", func(p *model.ServiceState) { p.SubState = "a b" }, "sub_state"},
		{"sub_state too long", func(p *model.ServiceState) { p.SubState = strings.Repeat("a", 65) }, "sub_state"},
		{"active_enter_at with offset", func(p *model.ServiceState) { p.ActiveEnterAt = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }, "active_enter_at"},
	}
	runInvalid(t, validServiceState, cases)
}
