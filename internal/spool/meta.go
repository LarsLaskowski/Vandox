package spool

import "time"

// ClockBound returns the lower bound for sequence numbers derived from the time now.
func ClockBound(_ time.Time) uint64 {
	return 0
}
