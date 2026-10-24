package spool

import (
	"context"
	"slices"
	"sort"
)

// mergeRanges returns the union of two sorted sets of ranges as one sorted set of ranges that neither overlap nor touch.
func mergeRanges(a, b []ackRange) []ackRange {
	all := slices.Concat(a, b)
	slices.SortFunc(all, func(x, y ackRange) int {
		switch {
		case x.First < y.First:
			return -1
		case x.First > y.First:
			return 1
		default:
			return 0
		}
	})
	out := make([]ackRange, 0, len(all))
	for _, r := range all {
		if n := len(out); n > 0 && r.First <= out[n-1].Last+1 {
			out[n-1].Last = max(out[n-1].Last, r.Last)

			continue
		}
		out = append(out, r)
	}

	return out
}

// isAcked reports whether the number seq is inside one of the ranges.
func isAcked(ranges []ackRange, seq uint64) bool {
	i := sort.Search(len(ranges), func(i int) bool { return ranges[i].Last >= seq })

	return i < len(ranges) && ranges[i].First <= seq
}

// covers reports whether the numbers first to last are all acknowledged.
func covers(ranges []ackRange, first, last uint64) bool {
	i := sort.Search(len(ranges), func(i int) bool { return ranges[i].Last >= first })

	return i < len(ranges) && ranges[i].First <= first && ranges[i].Last >= last
}

// countAbove returns the number of ranges above the oldest kept number lo: a range that starts at or below it leaves no
// unacknowledged record beneath it and does not count.
func countAbove(ranges []ackRange, lo uint64) int {
	n := 0
	for _, r := range ranges {
		if r.First > lo {
			n++
		}
	}

	return n
}

// trimBelow drops the part of the ranges below lo.
func trimBelow(ranges []ackRange, lo uint64) []ackRange {
	var out []ackRange
	for _, r := range ranges {
		if r.Last < lo {
			continue
		}
		out = append(out, ackRange{First: max(r.First, lo), Last: r.Last})
	}

	return out
}

// gaps returns the numbers first to last that no range covers, as runs.
func gaps(ranges []ackRange, first, last uint64) []ackRange {
	var out []ackRange
	at := first
	for _, r := range ranges {
		if r.Last < at {
			continue
		}
		if r.First > last {
			break
		}
		if r.First > at {
			out = append(out, ackRange{First: at, Last: r.First - 1})
		}
		at = r.Last + 1
		if at > last {
			return out
		}
	}
	if at <= last {
		out = append(out, ackRange{First: at, Last: last})
	}

	return out
}

// issued returns the numbers first to last that a kept file holds, as ranges.
func (s *Spool) issued(first, last uint64) []ackRange {
	var out []ackRange
	for _, f := range s.files {
		if f.last == 0 {
			continue
		}
		lo, hi := max(first, f.first), min(last, f.last)
		if lo <= hi {
			out = append(out, ackRange{First: lo, Last: hi})
		}
	}

	return out
}

// oldestKept returns the lowest number of the kept files, or 0 when there is none.
func (s *Spool) oldestKept() uint64 {
	for _, f := range s.files {
		if f.last != 0 {
			return f.first
		}
	}

	return 0
}

// Ack acknowledges the sequence numbers first to last.
func (s *Spool) Ack(ctx context.Context, first, last uint64) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		return errClosed
	}
	if first == 0 || first > last {
		return nil
	}
	sub := s.issued(first, last)
	if len(sub) == 0 {
		return nil
	}
	merged := mergeRanges(s.acked, sub)
	if slices.Equal(merged, s.acked) {
		return nil
	}
	if countAbove(merged, s.oldestKept()) > MaxAckRanges {
		return ErrTooManyRanges
	}
	s.acked = merged
	s.reap()

	return s.writeMeta(false, false)
}

// reap deletes the data files whose numbers are all acknowledged, and the ones damaged as a whole, and trims the
// acknowledged ranges below the oldest kept number. The active file is never deleted.
func (s *Spool) reap() {
	kept := make([]*segFile, 0, len(s.files))
	for _, f := range s.files {
		if f != s.act && (f.last == 0 || covers(s.acked, f.first, f.last)) && s.removeFile(f) == nil {
			continue
		}
		kept = append(kept, f)
	}
	s.files = kept
	if lo := s.oldestKept(); lo != 0 {
		s.acked = trimBelow(s.acked, lo)
	}
}

// removeFile deletes the data file from the directory and forgets what is cached of it.
func (s *Spool) removeFile(f *segFile) error {
	if err := s.dir.removeTrusted(f.name(), &f.id); err != nil {
		return err
	}
	s.forget(f.num)
	s.dirNeed = true
	s.gen++

	return nil
}
