package sysstats

import (
	"context"
	"errors"
	"fmt"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// maxSyncError is the maximum error below which the clock counts as synchronized: the rule timedatectl uses.
const maxSyncError = 16 * time.Second

// Status bits of adjtimex.
const (
	staPLL  = 0x0001 // the phase-locked loop is on
	staNano = 0x2000 // the offset is in nanoseconds
)

// errNegativeEstimate is the error of a source that reports a negative error estimate.
var errNegativeEstimate = errors.New("sysstats: adjtimex: negative error estimate")

// ClockState is the kernel's clock discipline as adjtimex reports it.
type ClockState struct {
	MaxError       time.Duration // maximum error
	EstimatedError time.Duration // estimated error
	PLLOffset      time.Duration // the correction the phase-locked loop still has to apply
	PLL            bool          // whether the phase-locked loop is on
}

// ReadClock reads the kernel's clock discipline with adjtimex without changing it: a zero Timex has mode 0, which only reads.
func ReadClock() (ClockState, error) {
	var tx syscall.Timex
	if _, err := syscall.Adjtimex(&tx); err != nil {
		return ClockState{}, fmt.Errorf("sysstats: adjtimex: %w", err)
	}
	offsetUnit := time.Microsecond
	if tx.Status&staNano != 0 {
		offsetUnit = time.Nanosecond
	}
	return ClockState{
		MaxError:       time.Duration(tx.Maxerror) * time.Microsecond,
		EstimatedError: time.Duration(tx.Esterror) * time.Microsecond,
		PLLOffset:      time.Duration(tx.Offset) * offsetUnit,
		PLL:            tx.Status&staPLL != 0,
	}, nil
}

// Clock collects the clock synchronization; it implements scheduler.Collector.
type Clock struct {
	src Sources
}

// NewClock returns the clock collector reading src.
func NewClock(src Sources) *Clock {
	return &Clock{src: src.withDefaults()}
}

// Name returns ClockName.
func (c *Clock) Name() string {
	return ClockName
}

// Collect takes one sample of the clock synchronization.
func (c *Clock) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	state, err := c.src.Clock()
	if err != nil {
		return nil, err
	}
	if state.MaxError < 0 || state.EstimatedError < 0 {
		return nil, errNegativeEstimate
	}
	r := &recorder{run: run}
	synchronized := 0.0
	if state.MaxError < maxSyncError {
		synchronized = 1
	}
	r.add("clock_synchronized", synchronized, "")
	r.add("clock_max_error_seconds", state.MaxError.Seconds(), "seconds")
	r.add("clock_estimated_error_seconds", state.EstimatedError.Seconds(), "seconds")
	if state.PLL {
		r.add("clock_pll_offset_seconds", state.PLLOffset.Seconds(), "seconds")
	}
	return r.records, nil
}
