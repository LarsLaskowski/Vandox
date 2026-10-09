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
		{"pid negative", func(p *model.LogLine) { p.PID = -1 }, "pid"},
		{"priority too high", func(p *model.LogLine) { p.Priority = ptr(uint8(8)) }, "priority"},
		{"message too long", func(p *model.LogLine) { p.Message = strings.Repeat("m", model.MaxTextBytes+1) }, "message"},
	}
	runInvalid(t, validLogLine, cases)
}
