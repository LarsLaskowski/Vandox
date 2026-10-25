package spool

import (
	"context"
	"errors"
	"testing"
)

// requireAckRanges fails the test unless the spool reports want acknowledged ranges above the oldest kept number.
func requireAckRanges(t *testing.T, sp *Spool, when string, want int) {
	t.Helper()

	got := sp.AckRanges()
	expect(t, got == want, "AckRanges() %s = %d, want %d", when, got, want)
}

func TestAckRanges_CountsRangesAboveOldestKept(t *testing.T) {
	t.Run("few ranges and the oldest record", testAckRangesFew)
	t.Run("at the cap and after a refusal", testAckRangesCap)
}

func testAckRangesFew(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "t", 200, 60)

	requireAckRanges(t, sp, "of a spool with nothing acknowledged", 0)

	ackSingles(t, sp, all, 1)
	requireAckRanges(t, sp, "after one range above the oldest record", 1)

	ackSingles(t, sp, all, 3)
	requireAckRanges(t, sp, "after a second disjoint range", 2)

	ackSingles(t, sp, all, 2)
	requireAckRanges(t, sp, "after the gap between both ranges is acknowledged", 1)

	ackSingles(t, sp, all, 1)
	requireAckRanges(t, sp, "after a repeated acknowledgement", 1)

	ackSingles(t, sp, all, 0)
	requireAckRanges(t, sp, "after the oldest record is acknowledged and the range reaches it", 0)

	sp = r.reopen(sp)
	requireAckRanges(t, sp, "after a restart", 0)
}

func testAckRangesCap(t *testing.T) {
	r := newRig(t)
	sp := r.open()
	all := putTagged(t, sp, epoch, "t", 200, 60)

	var odd []int
	for i := range MaxAckRanges {
		odd = append(odd, 2*i+1)
	}
	ackSingles(t, sp, all, odd...)
	requireAckRanges(t, sp, "at the cap of disjoint ranges", MaxAckRanges)

	extra := all[2*MaxAckRanges+2]
	err := sp.Ack(context.Background(), extra.Seq, extra.Seq)
	expect(t, errors.Is(err, ErrTooManyRanges), "Ack of a disjoint range over the cap = %v, want ErrTooManyRanges", err)
	requireAckRanges(t, sp, "after the refused acknowledgement", MaxAckRanges)

	sp = r.reopen(sp)
	requireAckRanges(t, sp, "after a restart at the cap", MaxAckRanges)

	ackSingles(t, sp, all, 0)
	requireAckRanges(t, sp, "after the first range joined the oldest record", MaxAckRanges-1)
}
