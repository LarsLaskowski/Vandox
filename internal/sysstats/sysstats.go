// Package sysstats collects the system metrics of the monitored server from /proc, /sys and the read-only system calls statfs
// and adjtimex (record 0111).
package sysstats

import (
	"errors"
	"io/fs"
	"time"
)

// Source is the source of every record of the system collectors.
const Source = "system"

// Collector names.
const (
	SystemName     = "system"
	FilesystemName = "filesystem"
	ClockName      = "clock"
)

// MaxDisks is the most disks a system sample records.
const MaxDisks = 16

// errNotImplemented is returned by every body of the skeleton.
var errNotImplemented = errors.New("not implemented")

// Sources are what the collectors read; a nil field takes its default.
type Sources struct {
	Proc   fs.FS                              // default os.DirFS("/proc")
	Sys    fs.FS                              // default os.DirFS("/sys")
	StatFS func(path string) (FSStats, error) // default StatFS
	Clock  func() (ClockState, error)         // default ReadClock
}

// FSStats is what statfs reports about a file system; the counts are in units of BlockSize.
type FSStats struct {
	BlockSize       uint64
	Blocks          uint64
	BlocksFree      uint64
	BlocksAvailable uint64
	Inodes          uint64
	InodesFree      uint64
}

// StatFS returns the statistics of the file system that holds path; its error is the bare errno, without the path.
func StatFS(path string) (FSStats, error) {
	return FSStats{}, errNotImplemented
}

// ClockState is the kernel's clock discipline as adjtimex reports it.
type ClockState struct {
	MaxError       time.Duration // maximum error
	EstimatedError time.Duration // estimated error
	PLLOffset      time.Duration // the correction the phase-locked loop still has to apply
	PLL            bool          // whether the phase-locked loop is on
}

// ReadClock reads the kernel's clock discipline with adjtimex without changing it.
func ReadClock() (ClockState, error) {
	return ClockState{}, errNotImplemented
}
