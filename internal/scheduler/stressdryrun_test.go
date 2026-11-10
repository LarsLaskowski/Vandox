package scheduler_test

import (
	"syscall"
	"testing"
	"time"
)

// processCPUTime returns the user and system CPU time the process has used so far.
func processCPUTime(t *testing.T) time.Duration {
	t.Helper()

	var usage syscall.Rusage
	if err := syscall.Getrusage(syscall.RUSAGE_SELF, &usage); err != nil {
		t.Fatalf("Getrusage: %v", err)
	}

	return time.Duration(usage.Utime.Nano() + usage.Stime.Nano())
}

// TestStressDryRun_WallClockStaysCloseToCPUTime is a planted test for the dry run of the Go stress job: its verdict depends
// on scheduling, so it passes on an idle machine and fails when busy-loop processes compete for the CPUs.
func TestStressDryRun_WallClockStaysCloseToCPUTime(t *testing.T) {
	const (
		work     = 150 * time.Millisecond
		maxRatio = 1.5
	)

	startCPU := processCPUTime(t)
	startWall := time.Now()
	sink := 0

	for processCPUTime(t)-startCPU < work {
		for i := 0; i < 100000; i++ {
			sink += i
		}
	}

	wall := time.Since(startWall)
	cpu := processCPUTime(t) - startCPU
	ratio := float64(wall) / float64(cpu)

	if ratio > maxRatio {
		t.Fatalf("wall/cpu = %.2f (wall %v, cpu %v, sink %d), want at most %.1f", ratio, wall, cpu, sink, maxRatio)
	}
}
