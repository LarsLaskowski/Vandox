package model_test

import (
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validMariaDBStatus() *model.MariaDBStatus {
	return &model.MariaDBStatus{
		Availability: model.MariaDBUp,
		PingLatency:  ptr(5 * time.Millisecond),
		Status:       map[string]uint64{"Uptime": 100, "Threads_connected": 3},
		Variables:    map[string]string{"max_connections": "151"},
		Threads: []model.MariaDBThread{{
			ID:          7,
			User:        "root",
			Host:        "localhost",
			DB:          "shop",
			Command:     "Query",
			TimeSeconds: 3,
			State:       "executing",
			Info:        "SELECT 1",
			Truncated:   true,
		}},
		Complete: true,
	}
}

func TestMariaDBStatus_Kind(t *testing.T) {
	if got := validMariaDBStatus().Kind(); got != model.KindMariaDBStatus {
		t.Errorf("Kind() = %q, want %q", got, model.KindMariaDBStatus)
	}
}

func TestMariaDBStatus_Validate_Valid(t *testing.T) {
	tests := []struct {
		name string
		p    *model.MariaDBStatus
	}{
		{"fully populated", validMariaDBStatus()},
		{"minimal up", &model.MariaDBStatus{Availability: model.MariaDBUp}},
		{"down without data", &model.MariaDBStatus{Availability: model.MariaDBDown}},
		{"not answering with ping latency", &model.MariaDBStatus{Availability: model.MariaDBNotAnswering, PingLatency: ptr(time.Duration(0))}},
		{"key of max length", &model.MariaDBStatus{Availability: model.MariaDBUp, Status: map[string]uint64{"A" + strings.Repeat("b", model.MaxNameBytes-1): 1}}},
		{"variable value of max short text", &model.MariaDBStatus{Availability: model.MariaDBUp, Variables: map[string]string{"v": strings.Repeat("x", model.MaxShortTextBytes)}}},
		{"info of max text", &model.MariaDBStatus{Availability: model.MariaDBUp, Threads: []model.MariaDBThread{{ID: 1, Info: strings.Repeat("x", model.MaxTextBytes)}}}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}
}

func TestMariaDBStatus_Validate_Invalid(t *testing.T) {
	statusN := func(n int) map[string]uint64 {
		m := make(map[string]uint64, n)
		for i := range n {
			m["K"+strconv.Itoa(i)] = 1
		}
		return m
	}
	variablesN := func(n int) map[string]string {
		m := make(map[string]string, n)
		for i := range n {
			m["K"+strconv.Itoa(i)] = "v"
		}
		return m
	}
	longKey := strings.Repeat("k", 200)
	longKeyPath := strconv.Quote(strings.Repeat("k", 128)) + "..."
	thread := func(f func(th *model.MariaDBThread)) func(p *model.MariaDBStatus) {
		return func(p *model.MariaDBStatus) { f(&p.Threads[0]) }
	}
	cases := []invalidCase[*model.MariaDBStatus]{
		{"availability empty", func(p *model.MariaDBStatus) { p.Availability = "" }, "availability"},
		{"availability upper case", func(p *model.MariaDBStatus) { p.Availability = "UP" }, "availability"},
		{"availability unknown", func(p *model.MariaDBStatus) { p.Availability = "bogus" }, "availability"},
		{"ping latency negative", func(p *model.MariaDBStatus) { p.PingLatency = ptr(-time.Nanosecond) }, "ping_latency_ns"},

		{"too many status entries", func(p *model.MariaDBStatus) { p.Status = statusN(model.MaxItems + 1) }, "status"},
		{"too many variables", func(p *model.MariaDBStatus) { p.Variables = variablesN(model.MaxItems + 1) }, "variables"},
		{"too many threads", func(p *model.MariaDBStatus) { p.Threads = repeat(model.MariaDBThread{ID: 1}, model.MaxItems+1) }, "threads"},

		{"status key with newline", func(p *model.MariaDBStatus) { p.Status = map[string]uint64{"a\nb": 1} }, `status["a\nb"]`},
		{"status key too long", func(p *model.MariaDBStatus) { p.Status = map[string]uint64{longKey: 1} }, "status[" + longKeyPath + "]"},
		{"status key starts with digit", func(p *model.MariaDBStatus) { p.Status = map[string]uint64{"1abc": 1} }, `status["1abc"]`},
		{"status key with dash", func(p *model.MariaDBStatus) { p.Status = map[string]uint64{"a-b": 1} }, `status["a-b"]`},
		{"variables key with newline", func(p *model.MariaDBStatus) { p.Variables = map[string]string{"a\nb": "v"} }, `variables["a\nb"]`},
		{"variables key too long", func(p *model.MariaDBStatus) { p.Variables = map[string]string{longKey: "v"} }, "variables[" + longKeyPath + "]"},
		{"variables key starts with underscore", func(p *model.MariaDBStatus) { p.Variables = map[string]string{"_x": "v"} }, `variables["_x"]`},
		{"variables value too long", func(p *model.MariaDBStatus) {
			p.Variables = map[string]string{"max_connections": strings.Repeat("v", model.MaxShortTextBytes+1)}
		}, `variables["max_connections"]`},

		{"down with status", func(p *model.MariaDBStatus) {
			p.Availability, p.Variables, p.Threads = model.MariaDBDown, nil, nil
		}, "availability"},
		{"down with variables", func(p *model.MariaDBStatus) {
			p.Availability, p.Status, p.Threads = model.MariaDBDown, nil, nil
		}, "availability"},
		{"down with threads", func(p *model.MariaDBStatus) {
			p.Availability, p.Status, p.Variables = model.MariaDBDown, nil, nil
		}, "availability"},
		{"not answering with threads", func(p *model.MariaDBStatus) {
			p.Availability, p.Status, p.Variables = model.MariaDBNotAnswering, nil, nil
		}, "availability"},

		{"thread user too long", thread(func(th *model.MariaDBThread) { th.User = strings.Repeat("u", model.MaxShortTextBytes+1) }), "threads[0].user"},
		{"thread host too long", thread(func(th *model.MariaDBThread) { th.Host = strings.Repeat("h", model.MaxShortTextBytes+1) }), "threads[0].host"},
		{"thread db too long", thread(func(th *model.MariaDBThread) { th.DB = strings.Repeat("d", model.MaxShortTextBytes+1) }), "threads[0].db"},
		{"thread command too long", thread(func(th *model.MariaDBThread) { th.Command = strings.Repeat("c", model.MaxShortTextBytes+1) }), "threads[0].command"},
		{"thread state too long", thread(func(th *model.MariaDBThread) { th.State = strings.Repeat("s", model.MaxShortTextBytes+1) }), "threads[0].state"},
		{"thread info too long", thread(func(th *model.MariaDBThread) { th.Info = strings.Repeat("i", model.MaxTextBytes+1) }), "threads[0].info"},
	}
	runInvalid(t, validMariaDBStatus, cases)
}
