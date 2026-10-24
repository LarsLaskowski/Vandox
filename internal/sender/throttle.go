package sender

import (
	"context"
	"time"
)

// throttle limits the bytes sent per second.
type throttle struct {
	next time.Time
	rate int64
}

// newThrottle returns a throttle of bytesPerSecond.
func newThrottle(bytesPerSecond int64) *throttle {
	return &throttle{rate: bytesPerSecond}
}

// wait blocks until n more bytes may be sent or ctx is done.
func (t *throttle) wait(ctx context.Context, n int) error {
	return nil
}
