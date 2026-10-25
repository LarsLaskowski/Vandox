package sender

import (
	"context"
	"sync"
	"time"
)

// throttle limits the bytes sent per second: a body is released when the previous one has had the time its bytes take
// at the rate, so the bodies of any span T sum to at most the rate times T plus one batch.
type throttle struct {
	mu   sync.Mutex
	next time.Time
	rate int64
}

// newThrottle returns a throttle of bytesPerSecond.
func newThrottle(bytesPerSecond int64) *throttle {
	return &throttle{rate: max(bytesPerSecond, 1)}
}

// wait blocks until n more bytes may be sent or ctx is done; it returns the context's error then.
func (t *throttle) wait(ctx context.Context, n int) error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if d := time.Until(t.next); d > 0 {
		timer := time.NewTimer(d)
		defer timer.Stop()
		select {
		case <-timer.C:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	start := time.Now()
	if t.next.After(start) {
		start = t.next
	}
	t.next = start.Add(time.Duration(int64(n)) * time.Second / time.Duration(t.rate))

	return nil
}
