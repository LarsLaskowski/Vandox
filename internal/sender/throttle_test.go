package sender

import (
	"context"
	"errors"
	"math/rand/v2"
	"testing"
	"time"
)

// sendTime returns how long n bytes take at rate bytes per second.
func sendTime(n int, rate int64) time.Duration {
	return time.Duration(int64(n)) * time.Second / time.Duration(rate)
}

func TestThrottle_BytesPerWindow(t *testing.T) {
	t.Run("bodies of any span sum to at most the rate times the span plus one batch", func(t *testing.T) {
		bubble(t, testThrottleWindows)
	})
	t.Run("a pause earns no burst", func(t *testing.T) {
		bubble(t, testThrottlePause)
	})
	t.Run("a cancelled wait returns the context's error at once", func(t *testing.T) {
		bubble(t, testThrottleCancel)
	})
}

// testThrottleWindows waits for bodies of varied sizes, with idle pauses between some, and checks every span.
func testThrottleWindows(t *testing.T) {
	const rate = 1000
	rng := rand.New(rand.NewPCG(3, 5))
	th := newThrottle(rate)
	ctx := context.Background()
	start := time.Now()
	var sizes []int
	var released []time.Time
	for range 60 {
		n := 100 + rng.IntN(2900)
		if err := th.wait(ctx, n); err != nil {
			t.Fatalf("wait(%d) error = %v, want nil", n, err)
		}
		sizes, released = append(sizes, n), append(released, time.Now())
		if rng.IntN(4) == 0 {
			time.Sleep(time.Duration(rng.IntN(2000)) * time.Millisecond)
		}
	}

	if !released[0].Equal(start) {
		t.Errorf("the first wait returned after %v, want at once (one batch is the allowed burst)", released[0].Sub(start))
	}
	for i := range released {
		for j := i + 1; j < len(released); j++ {
			sum := 0
			for _, n := range sizes[i:j] {
				sum += n
			}
			if span := released[j].Sub(released[i]); sendTime(sum, rate) > span {
				t.Fatalf("bodies %d to %d hold %d bytes released within %v, want at most %d bytes per second (%v)", i, j-1, sum, span, rate, sendTime(sum, rate))
			}
		}
	}
}

// testThrottlePause checks that idle time is not saved up: after a pause the next body goes at once, the one after it waits.
func testThrottlePause(t *testing.T) {
	const rate = 1000
	th := newThrottle(rate)
	ctx := context.Background()
	steps := []struct {
		pause time.Duration
		want  time.Duration
	}{
		{0, 0},
		{10 * time.Second, 0},
		{0, sendTime(500, rate)},
		{0, sendTime(500, rate)},
	}
	for i, st := range steps {
		time.Sleep(st.pause)
		before := time.Now()
		if err := th.wait(ctx, 500); err != nil {
			t.Fatalf("wait #%d error = %v, want nil", i, err)
		}
		if got := time.Since(before); got != st.want {
			t.Errorf("wait #%d after a pause of %v blocked %v, want %v", i, st.pause, got, st.want)
		}
	}
}

// testThrottleCancel checks that a wait that would block ends with the context.
func testThrottleCancel(t *testing.T) {
	th := newThrottle(1000)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if err := th.wait(ctx, 1000); err != nil {
		t.Fatalf("first wait error = %v, want nil", err)
	}
	started := time.Now()
	errc := make(chan error, 1)
	go func() { errc <- th.wait(ctx, 1000) }()
	advance(100 * time.Millisecond)
	cancel()

	err := <-errc

	if !errors.Is(err, context.Canceled) {
		t.Errorf("wait() after the context was cancelled = %v, want context.Canceled", err)
	}
	if got := time.Since(started); got != 100*time.Millisecond {
		t.Errorf("the cancelled wait ended after %v, want when the context was cancelled (100ms)", got)
	}
}
