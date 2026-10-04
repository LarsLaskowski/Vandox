package model_test

import (
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func newValidGap() *model.Gap { return validGap(model.GapAgentNotRunning) }

func TestGap_Kind(t *testing.T) {
	if got := newValidGap().Kind(); got != model.KindGap {
		t.Errorf("Kind() = %q, want %q", got, model.KindGap)
	}
}

func TestGap_Validate_Valid(t *testing.T) {
	for _, cause := range []model.GapCause{
		model.GapAgentNotRunning, model.GapSpoolDropped, model.GapCollectorTimeout,
		model.GapSequenceMissing, model.GapNoData, model.GapUnknown,
	} {
		t.Run("cause "+string(cause), func(t *testing.T) { requireValid(t, validGap(cause)) })
	}

	tests := []struct {
		name string
		p    *model.Gap
	}{
		{"fully populated", &model.Gap{From: testTime, To: testTime.Add(time.Minute), Cause: model.GapCollectorTimeout, Collector: "proc.meminfo", FirstSeq: 3, LastSeq: 9}},
		{"first_seq equals last_seq", &model.Gap{From: testTime, To: testTime.Add(time.Second), Cause: model.GapSpoolDropped, FirstSeq: 4, LastSeq: 4}},
		{"collector of max length", &model.Gap{From: testTime, To: testTime.Add(time.Second), Cause: model.GapUnknown, Collector: strings.Repeat("c", model.MaxNameBytes)}},
		{"one nanosecond long", &model.Gap{From: testTime, To: testTime.Add(time.Nanosecond), Cause: model.GapUnknown}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}
}

func TestGap_Validate_Invalid(t *testing.T) {
	cases := []invalidCase[*model.Gap]{
		{"from zero", func(g *model.Gap) { g.From = time.Time{} }, "from"},
		{"from with offset", func(g *model.Gap) { g.From = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }, "from"},
		{"to zero", func(g *model.Gap) { g.To = time.Time{} }, "to"},
		{"to with offset", func(g *model.Gap) { g.To = time.Date(2026, 3, 1, 15, 0, 0, 0, plusTwo) }, "to"},
		{"to equals from", func(g *model.Gap) { g.To = g.From }, "to"},
		{"to before from", func(g *model.Gap) { g.To = g.From.Add(-time.Second) }, "to"},
		{"cause empty", func(g *model.Gap) { g.Cause = "" }, "cause"},
		{"cause unknown", func(g *model.Gap) { g.Cause = "bogus" }, "cause"},
		{"cause upper case", func(g *model.Gap) { g.Cause = "UNKNOWN" }, "cause"},
		{"collector starts with dash", func(g *model.Gap) { g.Collector = "-x" }, "collector"},
		{"collector too long", func(g *model.Gap) { g.Collector = strings.Repeat("c", model.MaxNameBytes+1) }, "collector"},
		{"collector_timeout without collector", func(g *model.Gap) { g.Cause, g.Collector = model.GapCollectorTimeout, "" }, "collector"},
		{"first_seq without last_seq", func(g *model.Gap) { g.FirstSeq, g.LastSeq = 5, 0 }, "last_seq"},
		{"last_seq without first_seq", func(g *model.Gap) { g.FirstSeq, g.LastSeq = 0, 5 }, "last_seq"},
		{"first_seq after last_seq", func(g *model.Gap) { g.FirstSeq, g.LastSeq = 9, 5 }, "last_seq"},
		{"spool_dropped without sequence range", func(g *model.Gap) {
			g.Cause, g.FirstSeq, g.LastSeq = model.GapSpoolDropped, 0, 0
		}, "first_seq"},
		{"sequence_missing without sequence range", func(g *model.Gap) {
			g.Cause, g.FirstSeq, g.LastSeq = model.GapSequenceMissing, 0, 0
		}, "first_seq"},
	}
	runInvalid(t, newValidGap, cases)
}
