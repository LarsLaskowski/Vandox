package model_test

import (
	"errors"
	"fmt"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// testTime is a fixed UTC time used by all model tests.
var testTime = time.Date(2026, time.March, 1, 12, 0, 0, 0, time.UTC)

// plusTwo is a non-UTC zone with a non-zero offset.
var plusTwo = time.FixedZone("", 2*3600)

func ptr[T any](v T) *T { return &v }

func repeat[T any](v T, n int) []T {
	out := make([]T, n)
	for i := range out {
		out[i] = v
	}
	return out
}

// requireFieldError asserts that err is a *model.FieldError matching ErrInvalid with the given field path.
func requireFieldError(t *testing.T, err error, field string) {
	t.Helper()
	_ = fieldErrorOf(t, err, field)
}

// fieldErrorOf is requireFieldError that also returns the error for further assertions.
func fieldErrorOf(t *testing.T, err error, field string) *model.FieldError {
	t.Helper()
	if err == nil {
		t.Fatalf("Validate() = nil, want field error for %q", field)
	}
	if !errors.Is(err, model.ErrInvalid) {
		t.Errorf("Validate() = %v, want errors.Is(err, model.ErrInvalid)", err)
	}
	var fe *model.FieldError
	if !errors.As(err, &fe) {
		t.Fatalf("Validate() = %T (%v), want *model.FieldError", err, err)
	}
	if fe.Field != field {
		t.Errorf("Validate() field = %q, want %q (reason %q)", fe.Field, field, fe.Reason)
	}
	return fe
}

func requireValid(t *testing.T, p model.Payload) {
	t.Helper()
	if err := p.Validate(); err != nil {
		t.Errorf("%T.Validate() = %v, want nil", p, err)
	}
}

type invalidCase[T model.Payload] struct {
	name   string
	mutate func(p T)
	field  string
}

// runInvalid applies each mutation to a fresh valid payload and expects the listed field to be rejected.
func runInvalid[T model.Payload](t *testing.T, newValid func() T, cases []invalidCase[T]) {
	t.Helper()
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			p := newValid()
			tc.mutate(p)
			requireFieldError(t, p.Validate(), tc.field)
		})
	}
}

func validMeta() model.Meta {
	return model.Meta{
		Origin:     model.OriginAgent,
		Source:     "proc.meminfo",
		Seq:        1,
		CapturedAt: testTime,
	}
}

func TestFieldError_Error(t *testing.T) {
	tests := []struct {
		name string
		fe   *model.FieldError
		want string
	}{
		{"with field", &model.FieldError{Field: "data.name", Reason: "required"}, "model: data.name: required"},
		{"without field", &model.FieldError{Reason: "required"}, "model: required"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := tc.fe.Error(); got != tc.want {
				t.Errorf("Error() = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestFieldError_Is(t *testing.T) {
	var err error = &model.FieldError{Field: "source", Reason: "required"}
	if !errors.Is(err, model.ErrInvalid) {
		t.Errorf("errors.Is(%v, model.ErrInvalid) = false, want true", err)
	}
	wrapped := fmt.Errorf("context: %w", err)
	var fe *model.FieldError
	if !errors.As(wrapped, &fe) {
		t.Fatalf("errors.As(%v, *FieldError) = false, want true", wrapped)
	}
	if fe.Field != "source" || fe.Reason != "required" {
		t.Errorf("recovered FieldError = {%q, %q}, want {\"source\", \"required\"}", fe.Field, fe.Reason)
	}
}

func TestQuoteName(t *testing.T) {
	atMax := strings.Repeat("k", model.MaxNameBytes)
	long := strings.Repeat("k", model.MaxNameBytes+1)
	huge := strings.Repeat("k", 5000)
	// 127 ASCII bytes plus a two-byte rune: the cut at 128 bytes falls inside the rune.
	split := strings.Repeat("a", model.MaxNameBytes-1) + "é"
	tests := []struct {
		name string
		in   string
		want string
	}{
		{"plain", "mount", `"mount"`},
		{"newline is escaped", "a\nb", `"a\nb"`},
		{"empty", "", `""`},
		{"exactly max bytes has no marker", atMax, strconv.Quote(atMax)},
		{"max plus one is cut", long, strconv.Quote(atMax) + "..."},
		{"5000 bytes is cut", huge, strconv.Quote(atMax) + "..."},
		{"cut inside a rune", split, strconv.Quote(split[:model.MaxNameBytes]) + "..."},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := model.QuoteName(tc.in)
			if got != tc.want {
				t.Errorf("QuoteName(len %d) = %q, want %q", len(tc.in), got, tc.want)
			}
		})
	}

	t.Run("split rune is shown as byte escape", func(t *testing.T) {
		got := model.QuoteName(split)
		if !strings.Contains(got, `\xc3`) || !strings.HasSuffix(got, `"...`) {
			t.Errorf("QuoteName(split rune) = %q, want a \\xc3 escape and the ... marker", got)
		}
	})

	t.Run("no control byte and bounded length", func(t *testing.T) {
		key := strings.Repeat("\x01", 5000) + "\n\r\x00"
		got := model.QuoteName(key)
		if !strings.HasPrefix(got, `"`) || !strings.HasSuffix(got, `"...`) {
			t.Fatalf("QuoteName(control bytes) = %q, want a quoted string followed by ...", got)
		}
		for i := 0; i < len(got); i++ {
			if got[i] < 0x20 {
				t.Fatalf("QuoteName(control bytes) holds byte 0x%02x at %d, want none below 0x20", got[i], i)
			}
		}
		if limit := 4*model.MaxNameBytes + 5; len(got) > limit {
			t.Errorf("len(QuoteName(control bytes)) = %d, want <= %d", len(got), limit)
		}
	})
}

func TestMeta_Validate(t *testing.T) {
	validCases := []struct {
		name   string
		mutate func(m *model.Meta)
	}{
		{"agent meta", func(*model.Meta) {}},
		{"source of max length", func(m *model.Meta) { m.Source = strings.Repeat("a", model.MaxNameBytes) }},
		{"source with allowed punctuation", func(m *model.Meta) { m.Source = "a0._:/@+-x" }},
		{"import origin with seq 0", func(m *model.Meta) { m.Origin = model.OriginImport; m.Seq = 0 }},
		{"backend origin with seq 0", func(m *model.Meta) { m.Origin = model.OriginBackend; m.Seq = 0 }},
		{"zero offset in a non-UTC location", func(m *model.Meta) { m.CapturedAt = time.Date(2026, 3, 1, 12, 0, 0, 0, time.FixedZone("", 0)) }},
	}
	for _, tc := range validCases {
		t.Run("valid "+tc.name, func(t *testing.T) {
			m := validMeta()
			tc.mutate(&m)
			if err := m.Validate(); err != nil {
				t.Errorf("Validate() = %v, want nil", err)
			}
		})
	}

	invalidCases := []struct {
		name   string
		mutate func(m *model.Meta)
		field  string
	}{
		{"origin empty", func(m *model.Meta) { m.Origin = "" }, "origin"},
		{"origin unknown", func(m *model.Meta) { m.Origin = "other" }, "origin"},
		{"origin upper case", func(m *model.Meta) { m.Origin = "Agent" }, "origin"},
		{"source empty", func(m *model.Meta) { m.Source = "" }, "source"},
		{"source too long", func(m *model.Meta) { m.Source = strings.Repeat("a", model.MaxNameBytes+1) }, "source"},
		{"source starts with dash", func(m *model.Meta) { m.Source = "-x" }, "source"},
		{"source with space", func(m *model.Meta) { m.Source = "a b" }, "source"},
		{"source with trailing newline", func(m *model.Meta) { m.Source = "a\n" }, "source"},
		{"captured_at zero", func(m *model.Meta) { m.CapturedAt = time.Time{} }, "captured_at"},
		{"captured_at with offset", func(m *model.Meta) { m.CapturedAt = time.Date(2026, 3, 1, 14, 0, 0, 0, plusTwo) }, "captured_at"},
		{"agent seq zero", func(m *model.Meta) { m.Seq = 0 }, "seq"},
		{"import seq set", func(m *model.Meta) { m.Origin = model.OriginImport; m.Seq = 1 }, "seq"},
		{"backend seq set", func(m *model.Meta) { m.Origin = model.OriginBackend; m.Seq = 1 }, "seq"},
	}
	for _, tc := range invalidCases {
		t.Run("invalid "+tc.name, func(t *testing.T) {
			m := validMeta()
			tc.mutate(&m)
			requireFieldError(t, m.Validate(), tc.field)
		})
	}
}

func validGap(cause model.GapCause) *model.Gap {
	g := &model.Gap{From: testTime, To: testTime.Add(time.Hour), Cause: cause}
	switch cause {
	case model.GapSequenceMissing, model.GapSpoolDropped:
		g.FirstSeq, g.LastSeq = 1, 5
	case model.GapCollectorTimeout:
		g.Collector = "proc.meminfo"
	}
	return g
}

func TestRecord_Validate(t *testing.T) {
	t.Run("valid record", func(t *testing.T) {
		r := model.Record{Meta: validMeta(), Data: validMetric()}
		if err := r.Validate(); err != nil {
			t.Errorf("Validate() = %v, want nil", err)
		}
	})

	t.Run("nil data", func(t *testing.T) {
		r := model.Record{Meta: validMeta()}
		requireFieldError(t, r.Validate(), "data")
	})

	t.Run("typed nil payload", func(t *testing.T) {
		r := model.Record{Meta: validMeta(), Data: (*model.MetricPoint)(nil)}
		requireFieldError(t, r.Validate(), "data")
	})

	t.Run("payload error is prefixed", func(t *testing.T) {
		snap := &model.ProcessSnapshot{Processes: []model.ProcessSample{
			{PID: 1, Command: "init"},
			{PID: 0, Command: "broken"},
		}}
		r := model.Record{Meta: validMeta(), Data: snap}
		requireFieldError(t, r.Validate(), "data.processes[1].pid")
	})

	t.Run("meta errors come first", func(t *testing.T) {
		m := validMeta()
		m.Source = ""
		r := model.Record{Meta: m, Data: nil}
		requireFieldError(t, r.Validate(), "source")
	})

	gapCases := []struct {
		name   string
		origin model.Origin
		seq    uint64
		cause  model.GapCause
		field  string
	}{
		{"agent sequence_missing", model.OriginAgent, 1, model.GapSequenceMissing, "data.cause"},
		{"agent no_data", model.OriginAgent, 1, model.GapNoData, "data.cause"},
		{"backend sequence_missing", model.OriginBackend, 0, model.GapSequenceMissing, ""},
		{"backend no_data", model.OriginBackend, 0, model.GapNoData, ""},
		{"agent agent_not_running", model.OriginAgent, 1, model.GapAgentNotRunning, ""},
	}
	for _, tc := range gapCases {
		t.Run("gap "+tc.name, func(t *testing.T) {
			m := validMeta()
			m.Origin, m.Seq = tc.origin, tc.seq
			r := model.Record{Meta: m, Data: validGap(tc.cause)}
			err := r.Validate()
			if tc.field == "" {
				if err != nil {
					t.Errorf("Validate() = %v, want nil", err)
				}
				return
			}
			requireFieldError(t, err, tc.field)
		})
	}

	t.Run("Kind returns the payload kind", func(t *testing.T) {
		r := model.Record{Meta: validMeta(), Data: validMetric()}
		if got := r.Kind(); got != model.KindMetric {
			t.Errorf("Kind() = %q, want %q", got, model.KindMetric)
		}
	})

	t.Run("Kind of nil data is empty", func(t *testing.T) {
		r := model.Record{Meta: validMeta()}
		if got := r.Kind(); got != "" {
			t.Errorf("Kind() = %q, want empty", got)
		}
	})
}

func TestPayload_NilReceiver(t *testing.T) {
	tests := []struct {
		name string
		p    model.Payload
	}{
		{"metric", (*model.MetricPoint)(nil)},
		{"process snapshot", (*model.ProcessSnapshot)(nil)},
		{"connection snapshot", (*model.ConnectionSnapshot)(nil)},
		{"service state", (*model.ServiceState)(nil)},
		{"mariadb status", (*model.MariaDBStatus)(nil)},
		{"kernel event", (*model.KernelEvent)(nil)},
		{"log line", (*model.LogLine)(nil)},
		{"gap", (*model.Gap)(nil)},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			fe := fieldErrorOf(t, tc.p.Validate(), "")
			if fe.Reason != "required" {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, "required")
			}
		})
	}
}
