package wire_test

import (
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const testBootID = "0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f"

// testTime is a fixed UTC time used by all wire tests.
var testTime = time.Date(2026, time.March, 1, 12, 0, 0, 0, time.UTC)

func ptr[T any](v T) *T { return &v }

func metricRecord(seq uint64) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "proc.meminfo", Seq: seq, CapturedAt: testTime},
		Data: &model.MetricPoint{Name: "mem_free_bytes", Value: 1024},
	}
}

// validBatch returns a valid live batch of n metric records with seq 1..n.
func validBatch(n int) *wire.Batch {
	b := &wire.Batch{Header: wire.NewHeader("agent-1", testBootID, wire.ModeLive)}
	for i := range n {
		b.Records = append(b.Records, metricRecord(uint64(i+1)))
	}
	return b
}

func requireFieldError(t *testing.T, err error, field string) {
	t.Helper()
	if err == nil {
		t.Fatalf("got nil error, want field error for %q", field)
	}
	if !errors.Is(err, model.ErrInvalid) {
		t.Errorf("got %v, want errors.Is(err, model.ErrInvalid)", err)
	}
	var fe *model.FieldError
	if !errors.As(err, &fe) {
		t.Fatalf("got %T (%v), want a *model.FieldError", err, err)
	}
	if fe.Field != field {
		t.Errorf("field = %q, want %q (reason %q)", fe.Field, field, fe.Reason)
	}
}

func TestDefaultLimits(t *testing.T) {
	got := wire.DefaultLimits()
	want := wire.Limits{MaxLineBytes: 1 << 20, MaxBatchBytes: 16 << 20, MaxRecords: 20000}
	if got != want {
		t.Errorf("DefaultLimits() = %+v, want %+v", got, want)
	}
}

func TestDecodeError(t *testing.T) {
	inner := errors.New("boom")
	var err error = &wire.DecodeError{Line: 3, Err: inner}
	if got, want := err.Error(), "wire: line 3: boom"; got != want {
		t.Errorf("Error() = %q, want %q", got, want)
	}
	if !errors.Is(err, inner) {
		t.Errorf("errors.Is(%v, inner) = false, want true", err)
	}
	var de *wire.DecodeError
	if !errors.As(err, &de) || de.Line != 3 {
		t.Errorf("errors.As(*DecodeError) = %v, line %v, want line 3", errors.As(err, &de), de)
	}
}

func TestRecordSizeError(t *testing.T) {
	tests := []struct {
		name string
		err  *wire.RecordSizeError
		want string
	}{
		{"with index", &wire.RecordSizeError{Index: 2, Size: 1500000, Limit: 1048576}, "wire: records[2]: encoded record is 1500000 bytes, limit 1048576"},
		{"without index", &wire.RecordSizeError{Index: -1, Size: 1500000, Limit: 1048576}, "wire: encoded record is 1500000 bytes, limit 1048576"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := tc.err.Error(); got != tc.want {
				t.Errorf("Error() = %q, want %q", got, tc.want)
			}
			var err error = tc.err
			if !errors.Is(err, wire.ErrRecordTooLarge) {
				t.Errorf("errors.Is(err, ErrRecordTooLarge) = false, want true")
			}
			if !errors.Is(err, wire.ErrLimitExceeded) {
				t.Errorf("errors.Is(err, ErrLimitExceeded) = false, want true")
			}
		})
	}
}

func TestNewHeader(t *testing.T) {
	h := wire.NewHeader("agent-1", testBootID, wire.ModeBackfill)
	if h.FormatMajor != 1 || h.FormatMinor != 0 {
		t.Errorf("NewHeader() format = %d.%d, want 1.0", h.FormatMajor, h.FormatMinor)
	}
	if h.AgentID != "agent-1" || h.BootID != testBootID || h.Mode != wire.ModeBackfill {
		t.Errorf("NewHeader() = %+v, want agent-1, %s, backfill", h, testBootID)
	}
	if h.ClockOffset != nil {
		t.Errorf("NewHeader() ClockOffset = %v, want nil", *h.ClockOffset)
	}
	if err := h.Validate(); err != nil {
		t.Errorf("NewHeader().Validate() = %v, want nil", err)
	}
	if wire.MajorVersion != 1 || wire.MinorVersion != 0 {
		t.Errorf("version constants = %d.%d, want 1.0", wire.MajorVersion, wire.MinorVersion)
	}
}

func TestHeader_Validate_Valid(t *testing.T) {
	tests := []struct {
		name   string
		mutate func(h *wire.Header)
	}{
		{"live", func(*wire.Header) {}},
		{"backfill", func(h *wire.Header) { h.Mode = wire.ModeBackfill }},
		{"clock offset nil", func(h *wire.Header) { h.ClockOffset = nil }},
		{"clock offset negative", func(h *wire.Header) { h.ClockOffset = ptr(-5 * time.Second) }},
		{"clock offset positive", func(h *wire.Header) { h.ClockOffset = ptr(5 * time.Second) }},
		{"clock offset zero", func(h *wire.Header) { h.ClockOffset = ptr(time.Duration(0)) }},
		{"agent id of 64 bytes", func(h *wire.Header) { h.AgentID = strings.Repeat("a", 64) }},
		{"agent id with allowed punctuation", func(h *wire.Header) { h.AgentID = "Web-01.example_net" }},
		{"minor version above 0", func(h *wire.Header) { h.FormatMinor = 7 }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := wire.NewHeader("agent-1", testBootID, wire.ModeLive)
			tc.mutate(&h)
			if err := h.Validate(); err != nil {
				t.Errorf("Validate() = %v, want nil", err)
			}
		})
	}
}

func TestHeader_Validate_Invalid(t *testing.T) {
	versionCases := []struct {
		name  string
		major int
	}{
		{"major 0", 0},
		{"major 2", 2},
		{"major negative", -1},
	}
	for _, tc := range versionCases {
		t.Run(tc.name, func(t *testing.T) {
			h := wire.NewHeader("agent-1", testBootID, wire.ModeLive)
			h.FormatMajor = tc.major
			err := h.Validate()
			if !errors.Is(err, wire.ErrUnsupportedVersion) {
				t.Fatalf("Validate() = %v, want errors.Is(err, ErrUnsupportedVersion)", err)
			}
			var fe *model.FieldError
			if errors.As(err, &fe) {
				t.Errorf("Validate() = %v, want a version error that is not a *model.FieldError", err)
			}
		})
	}

	fieldCases := []struct {
		name   string
		mutate func(h *wire.Header)
		field  string
	}{
		{"minor negative", func(h *wire.Header) { h.FormatMinor = -1 }, "format_minor"},
		{"agent id empty", func(h *wire.Header) { h.AgentID = "" }, "agent_id"},
		{"agent id 65 bytes", func(h *wire.Header) { h.AgentID = strings.Repeat("a", 65) }, "agent_id"},
		{"agent id with space", func(h *wire.Header) { h.AgentID = "a b" }, "agent_id"},
		{"agent id starts with dash", func(h *wire.Header) { h.AgentID = "-a" }, "agent_id"},
		{"agent id with trailing newline", func(h *wire.Header) { h.AgentID = "a\n" }, "agent_id"},
		{"agent id with slash", func(h *wire.Header) { h.AgentID = "a/b" }, "agent_id"},
		{"boot id upper case", func(h *wire.Header) { h.BootID = strings.ToUpper(testBootID) }, "boot_id"},
		{"boot id without dashes", func(h *wire.Header) { h.BootID = strings.ReplaceAll(testBootID, "-", "") }, "boot_id"},
		{"boot id with braces", func(h *wire.Header) { h.BootID = "{" + testBootID + "}" }, "boot_id"},
		{"boot id too short", func(h *wire.Header) { h.BootID = testBootID[:35] }, "boot_id"},
		{"boot id too long", func(h *wire.Header) { h.BootID = testBootID + "0" }, "boot_id"},
		{"boot id empty", func(h *wire.Header) { h.BootID = "" }, "boot_id"},
		{"mode empty", func(h *wire.Header) { h.Mode = "" }, "mode"},
		{"mode capitalized", func(h *wire.Header) { h.Mode = "Live" }, "mode"},
		{"mode unknown", func(h *wire.Header) { h.Mode = "replay" }, "mode"},
	}
	for _, tc := range fieldCases {
		t.Run(tc.name, func(t *testing.T) {
			h := wire.NewHeader("agent-1", testBootID, wire.ModeLive)
			tc.mutate(&h)
			requireFieldError(t, h.Validate(), tc.field)
		})
	}
}

func TestBatch_Validate_Valid(t *testing.T) {
	t.Run("one record", func(t *testing.T) {
		if err := validBatch(1).Validate(); err != nil {
			t.Errorf("Validate() = %v, want nil", err)
		}
	})
	t.Run("sequence gaps are allowed", func(t *testing.T) {
		b := validBatch(3)
		b.Records[0].Seq, b.Records[1].Seq, b.Records[2].Seq = 5, 9, 1000
		if err := b.Validate(); err != nil {
			t.Errorf("Validate() = %v, want nil", err)
		}
	})
	t.Run("max records", func(t *testing.T) {
		b := validBatch(wire.DefaultLimits().MaxRecords)
		if err := b.Validate(); err != nil {
			t.Errorf("Validate() with %d records = %v, want nil", len(b.Records), err)
		}
	})
}

func TestBatch_Validate_Invalid(t *testing.T) {
	t.Run("no records", func(t *testing.T) {
		b := validBatch(0)
		if err := b.Validate(); !errors.Is(err, wire.ErrEmptyBatch) {
			t.Errorf("Validate() = %v, want ErrEmptyBatch", err)
		}
	})

	t.Run("too many records", func(t *testing.T) {
		b := validBatch(wire.DefaultLimits().MaxRecords + 1)
		if err := b.Validate(); !errors.Is(err, wire.ErrLimitExceeded) {
			t.Errorf("Validate() = %v, want ErrLimitExceeded", err)
		}
	})

	t.Run("import origin", func(t *testing.T) {
		b := validBatch(3)
		b.Records[1].Origin, b.Records[1].Seq = model.OriginImport, 0
		requireFieldError(t, b.Validate(), "records[1].origin")
	})

	t.Run("backend origin", func(t *testing.T) {
		b := validBatch(3)
		b.Records[2].Origin, b.Records[2].Seq = model.OriginBackend, 0
		requireFieldError(t, b.Validate(), "records[2].origin")
	})

	seqCases := []struct {
		name string
		seqs []uint64
	}{
		{"equal sequence numbers", []uint64{1, 2, 2}},
		{"decreasing sequence numbers", []uint64{5, 4, 6}},
		{"equal first two", []uint64{7, 7}},
	}
	for _, tc := range seqCases {
		t.Run(tc.name, func(t *testing.T) {
			b := validBatch(len(tc.seqs))
			for i, s := range tc.seqs {
				b.Records[i].Seq = s
			}
			if err := b.Validate(); !errors.Is(err, wire.ErrSequence) {
				t.Errorf("Validate() = %v, want ErrSequence", err)
			}
		})
	}

	t.Run("invalid record is prefixed", func(t *testing.T) {
		b := validBatch(3)
		b.Records[2].Data = &model.MetricPoint{Name: ""}
		requireFieldError(t, b.Validate(), "records[2].data.name")
	})

	t.Run("record without data", func(t *testing.T) {
		b := validBatch(2)
		b.Records[0].Data = nil
		requireFieldError(t, b.Validate(), "records[0].data")
	})

	t.Run("header error is prefixed", func(t *testing.T) {
		b := validBatch(1)
		b.Header.AgentID = ""
		requireFieldError(t, b.Validate(), "header.agent_id")
	})
}
