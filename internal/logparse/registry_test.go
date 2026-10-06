package logparse_test

import (
	"context"
	"errors"
	"io"
	"reflect"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
)

// stubParser is a minimal logparse.Parser that rates every file with a fixed confidence and records what
// Detect was given.
type stubParser struct {
	typ        string
	confidence logparse.Confidence
	gotFile    logparse.File
	gotHead    []byte
	calls      int
}

func (p *stubParser) Type() string { return p.typ }

func (p *stubParser) Detect(f logparse.File, head []byte) logparse.Confidence {
	p.calls++
	p.gotFile = f
	p.gotHead = head
	return p.confidence
}

func (p *stubParser) Parse(context.Context, logparse.File, io.Reader, logparse.Emitter) error {
	return nil
}

func TestCheckType(t *testing.T) {
	tests := []struct {
		name string
		typ  string
		ok   bool
	}{
		{"single letter", "a", true},
		{"plain word", "syslog", true},
		{"dot and dash", "kern.log", true},
		{"underscore digits", "mariadb_error-10", true},
		{"64 bytes", "a" + strings.Repeat("b", 63), true},
		{"empty", "", false},
		{"upper case", "Syslog", false},
		{"leading digit", "1syslog", false},
		{"leading dash", "-syslog", false},
		{"65 bytes", "a" + strings.Repeat("b", 64), false},
		{"space", "sys log", false},
		{"slash", "sys/log", false},
		{"trailing newline", "syslog\n", false},
		{"non-ASCII", "syslög", false},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			err := logparse.CheckType(tc.typ)

			if tc.ok && err != nil {
				t.Errorf("CheckType(%q) = %v, want nil", tc.typ, err)
			}
			if !tc.ok && !errors.Is(err, logparse.ErrInvalidParser) {
				t.Errorf("CheckType(%q) = %v, want an error wrapping ErrInvalidParser", tc.typ, err)
			}
		})
	}
}

func TestNewRegistry_Refuses(t *testing.T) {
	tests := []struct {
		name    string
		parsers []logparse.Parser
	}{
		{"nil parser", []logparse.Parser{nil}},
		{"nil parser after a valid one", []logparse.Parser{&stubParser{typ: "a"}, nil}},
		{"empty type", []logparse.Parser{&stubParser{typ: ""}}},
		{"upper case type", []logparse.Parser{&stubParser{typ: "Syslog"}}},
		{"65 byte type", []logparse.Parser{&stubParser{typ: "a" + strings.Repeat("b", 64)}}},
		{"duplicate type", []logparse.Parser{&stubParser{typ: "syslog"}, &stubParser{typ: "mail"}, &stubParser{typ: "syslog"}}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			reg, err := logparse.NewRegistry(tc.parsers...)

			if !errors.Is(err, logparse.ErrInvalidParser) {
				t.Errorf("NewRegistry() error = %v, want an error wrapping ErrInvalidParser", err)
			}
			if reg != nil {
				t.Errorf("NewRegistry() = %v, want a nil registry with the error", reg)
			}
		})
	}
}

func TestNewRegistry_EmptyRegistryMatchesNothing(t *testing.T) {
	reg, err := logparse.NewRegistry()
	if err != nil || reg == nil {
		t.Fatalf("NewRegistry() = %v, %v, want a registry and nil", reg, err)
	}

	p, c := reg.Detect(logparse.File{Name: "syslog"}, []byte("x"))

	if p != nil || c != logparse.NoMatch {
		t.Errorf("Detect() on an empty registry = %v, %v, want nil, NoMatch", p, c)
	}
	if got := reg.Types(); len(got) != 0 {
		t.Errorf("Types() on an empty registry = %v, want none", got)
	}
}

// checkDetect registers parsers rated by rates and checks which one Detect picks; want is the index or -1.
func checkDetect(t *testing.T, rates []logparse.Confidence, want int) {
	t.Helper()
	parsers := make([]logparse.Parser, len(rates))
	for i, c := range rates {
		parsers[i] = &stubParser{typ: "t" + string(rune('a'+i)), confidence: c}
	}
	reg, err := logparse.NewRegistry(parsers...)
	if err != nil {
		t.Fatalf("NewRegistry() error = %v, want nil", err)
	}

	got, conf := reg.Detect(logparse.File{Name: "x"}, []byte("head"))

	if want < 0 {
		if got != nil || conf != logparse.NoMatch {
			t.Errorf("Detect() = %v, %v, want nil, NoMatch", got, conf)
		}
		return
	}
	if got != parsers[want] || conf != rates[want] {
		t.Errorf("Detect() = %v, %v, want parser %d (%s) with confidence %v", got, conf, want, parsers[want].Type(), rates[want])
	}
}

func TestRegistry_Detect(t *testing.T) {
	tests := []struct {
		name  string
		rates []logparse.Confidence
		want  int // index of the expected parser, -1 for none
	}{
		{"highest confidence wins", []logparse.Confidence{logparse.MatchName, logparse.MatchContent, logparse.NoMatch}, 1},
		{"later parser with higher confidence beats an earlier one", []logparse.Confidence{logparse.MatchName, logparse.NoMatch, logparse.MatchContent}, 2},
		{"tie goes to the first registered", []logparse.Confidence{logparse.NoMatch, logparse.MatchContent, logparse.MatchContent}, 1},
		{"tie at name match goes to the first registered", []logparse.Confidence{logparse.MatchName, logparse.MatchName}, 0},
		{"single match", []logparse.Confidence{logparse.NoMatch, logparse.MatchName}, 1},
		{"every parser returns NoMatch", []logparse.Confidence{logparse.NoMatch, logparse.NoMatch}, -1},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { checkDetect(t, tc.rates, tc.want) })
	}
}

func TestRegistry_Detect_PassesFileAndHeadUnchanged(t *testing.T) {
	a := &stubParser{typ: "a"}
	b := &stubParser{typ: "b", confidence: logparse.MatchName}
	reg, err := logparse.NewRegistry(a, b)
	if err != nil {
		t.Fatalf("NewRegistry() error = %v, want nil", err)
	}
	file := logparse.File{Name: "var/log/syslog.1", ModTime: time.Date(2026, 10, 6, 1, 2, 3, 4, time.UTC)}
	head := []byte("Oct  6 01:02:03 host kernel: x\n")
	want := slices.Clone(head)

	reg.Detect(file, head)

	if a.calls != 1 || b.calls != 1 {
		t.Fatalf("Detect() called the parsers %d and %d times, want once each", a.calls, b.calls)
	}

	for _, p := range []*stubParser{a, b} {
		if !reflect.DeepEqual(p.gotFile, file) {
			t.Errorf("parser %s got File %+v, want %+v", p.typ, p.gotFile, file)
		}
		if string(p.gotHead) != string(want) {
			t.Errorf("parser %s got head %q, want %q", p.typ, p.gotHead, want)
		}
	}
	if string(head) != string(want) {
		t.Errorf("head after Detect() = %q, want it unchanged %q", head, want)
	}
}

func TestRegistry_Types(t *testing.T) {
	reg, err := logparse.NewRegistry(&stubParser{typ: "syslog"}, &stubParser{typ: "mail"}, &stubParser{typ: "kern"})
	if err != nil {
		t.Fatalf("NewRegistry() error = %v, want nil", err)
	}

	got := reg.Types()

	if want := []string{"syslog", "mail", "kern"}; !slices.Equal(got, want) {
		t.Errorf("Types() = %v, want %v", got, want)
	}
}
