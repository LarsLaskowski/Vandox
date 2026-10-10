package model_test

import (
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validLogLine() *model.LogLine {
	return &model.LogLine{
		Log:       "journal",
		Host:      "web-1",
		Program:   "sshd",
		PID:       5120,
		Priority:  ptr(uint8(3)),
		Message:   "Failed password for root from 192.0.2.7",
		Truncated: true,
	}
}

func TestLogLine_Kind(t *testing.T) {
	if got := validLogLine().Kind(); got != model.KindLogLine {
		t.Errorf("Kind() = %q, want %q", got, model.KindLogLine)
	}
}

func TestLogLine_Validate_Valid(t *testing.T) {
	tests := []struct {
		name string
		p    *model.LogLine
	}{
		{"fully populated", validLogLine()},
		{"minimal with empty message", &model.LogLine{Log: "journal"}},
		{"file path as log", &model.LogLine{Log: "/var/log/maillog", Message: "x"}},
		{"priority upper bound", &model.LogLine{Log: "journal", Priority: ptr(uint8(7))}},
		{"priority zero", &model.LogLine{Log: "journal", Priority: ptr(uint8(0))}},
		{"message of max text", &model.LogLine{Log: "journal", Message: strings.Repeat("m", model.MaxTextBytes)}},
		{"no host", &model.LogLine{Log: "journal", Message: "x"}},
		{"host of max short text", &model.LogLine{Log: "journal", Host: strings.Repeat("h", model.MaxShortTextBytes)}},
		{"log of max short text", &model.LogLine{Log: strings.Repeat("l", model.MaxShortTextBytes)}},
		{"no event", &model.LogLine{Log: "journal", Message: "x"}},
		{"event of a lifecycle name", &model.LogLine{Log: "journal", Message: "x", Event: "mariadb.start"}},
		{"event of max name length", &model.LogLine{Log: "journal", Message: "x", Event: strings.Repeat("a", model.MaxNameBytes)}},
		{"message with control characters", &model.LogLine{Log: "journal", Message: "a\x00b\nc\x01"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}
}

func TestLogLine_Validate_Invalid(t *testing.T) {
	cases := []invalidCase[*model.LogLine]{
		{"log empty", func(p *model.LogLine) { p.Log = "" }, "log"},
		{"log too long", func(p *model.LogLine) { p.Log = strings.Repeat("l", model.MaxShortTextBytes+1) }, "log"},
		{"host too long", func(p *model.LogLine) { p.Host = strings.Repeat("h", model.MaxShortTextBytes+1) }, "host"},
		{"program too long", func(p *model.LogLine) { p.Program = strings.Repeat("p", model.MaxShortTextBytes+1) }, "program"},
		{"event too long", func(p *model.LogLine) { p.Event = strings.Repeat("a", model.MaxNameBytes+1) }, "event"},
		{"event with a space", func(p *model.LogLine) { p.Event = "mariadb start" }, "event"},
		{"event with a leading dash", func(p *model.LogLine) { p.Event = "-x" }, "event"},
		{"event with a non-ASCII letter", func(p *model.LogLine) { p.Event = "é" }, "event"},
		{"event with a trailing newline", func(p *model.LogLine) { p.Event = "a\n" }, "event"},
		{"pid negative", func(p *model.LogLine) { p.PID = -1 }, "pid"},
		{"priority too high", func(p *model.LogLine) { p.Priority = ptr(uint8(8)) }, "priority"},
		{"message too long", func(p *model.LogLine) { p.Message = strings.Repeat("m", model.MaxTextBytes+1) }, "message"},
	}
	runInvalid(t, validLogLine, cases)
}

func TestLogLine_Validate_EventReason(t *testing.T) {
	cases := []struct {
		name  string
		event string
		want  string
	}{
		{"129 bytes", strings.Repeat("a", model.MaxNameBytes+1), "too long"},
		{"space", "mariadb start", "invalid characters"},
		{"leading dash", "-x", "invalid characters"},
		{"non-ASCII", "é", "invalid characters"},
		{"trailing newline", "a\n", "invalid characters"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			l := validLogLine()
			l.Event = tc.event
			fe := fieldErrorOf(t, l.Validate(), "event")
			if fe.Reason != tc.want {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, tc.want)
			}
		})
	}
}

func TestLogLine_Validate_EventIsCheckedAfterProgram(t *testing.T) {
	l := validLogLine()
	l.Program = strings.Repeat("p", model.MaxShortTextBytes+1)
	l.Event = "bad event"
	requireFieldError(t, l.Validate(), "program")
}
