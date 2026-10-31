package logstages

import (
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	oomStart   = "[1.0] example invoked oom-killer: gfp_mask=0x100cca(GFP_HIGHUSER_MOVABLE), order=0, oom_score_adj=0"
	oomKill    = "[1.9] Out of memory: Killed process 4242 (example) total-vm:8400000kB, anon-rss:7400000kB, file-rss:0kB, shmem-rss:0kB, UID:110 pgtables:14600kB oom_score_adj:0"
	cutHere    = "------------[ cut here ]------------"
	endTrace   = "---[ end trace 0000000000000000 ]---"
	kernelProg = "kernel"
)

// kernel hands a kernel line of host web-1 to the stages.
func (f *feeder) kernel(at time.Duration, msg string) []Output {
	f.t.Helper()

	return f.add(at, kernelProg, 0, msg)
}

func TestKernel_APlainKernelLinePassesAtOnce(t *testing.T) {
	f := newFeeder(t)

	out := f.kernel(0, "[0.1] TCP: request_sock_TCP: Possible SYN flooding on port 80. Sending cookies.")

	wantMessages(t, "plain kernel line", out, "[0.1] TCP: request_sock_TCP: Possible SYN flooding on port 80. Sending cookies.")

	if f.s.Open() {
		t.Errorf("Open() = true after a plain kernel line, want false")
	}
}

func TestKernel_ReportsEndAtTheirEndLine(t *testing.T) {
	cases := []struct {
		name  string
		start string
		end   string
	}{
		{"oom kill", oomStart, oomKill},
		{"oom kill of the allocating task", oomStart, "[1.9] Out of memory (oom_kill_allocating_task): Killed process 4242 (example) total-vm:1kB"},
		{"oom kill of a cgroup", oomStart, "[1.9] Memory cgroup out of memory: Killed process 4242 (example) total-vm:1kB"},
		{"oom without a killable process", oomStart, "[1.9] Out of memory and no killable processes"},
		{"warning with a trace", cutHere, endTrace},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) { checkReportEnd(t, tc.start, tc.end) })
	}
}

func checkReportEnd(t *testing.T, start, end string) {
	t.Helper()

	f := newFeeder(t)
	f.add(-time.Second, "sshd", 812, "before")

	if out := f.kernel(0, start); len(out) != 0 {
		t.Fatalf("start gave %d outputs, want 0", len(out))
	}

	f.add(500*time.Millisecond, "sshd", 812, "interleaved line of another program")
	f.kernel(time.Second, "[1.1] middle line")

	out := f.kernel(2*time.Second, end)

	wantMessages(t, "end line ends the report", out, start+"\n[1.1] middle line\n"+end)

	if f.s.Open() {
		t.Errorf("Open() = true after the end line, want false")
	}

	rec := out[0].Rec
	l := logOf(t, out[0])

	if !rec.CapturedAt.Equal(base) || rec.Source != "journal" || rec.Origin != model.OriginAgent || rec.Seq != 102 {
		t.Errorf("report meta = %+v, want the meta of the first line (captured %v, seq 102)", rec.Meta, base)
	}

	if l.Program != kernelProg || l.PID != 0 || l.Host != "web-1" || l.Log != "journal" || l.Event != "" {
		t.Errorf("report line = %+v, want program kernel, pid 0, host web-1, log journal and no event", *l)
	}

	if want := base.Add(-time.Second); !out[0].From.Equal(want) {
		t.Errorf("From = %v, want the time of the line before the first line, %v", out[0].From, want)
	}

	wantMessages(t, "the next kernel line has no report", f.kernel(3*time.Second, "[1.2] after"), "[1.2] after")
}

func TestKernel_AReportIsNotEndedByTheMarkerOfTheOtherType(t *testing.T) {
	t.Run("an oom report goes on after an end trace", func(t *testing.T) {
		f := newFeeder(t)
		f.kernel(0, oomStart)

		if out := f.kernel(time.Second, endTrace); len(out) != 0 {
			t.Fatalf("end trace gave %d outputs, want 0 inside an oom report", len(out))
		}

		wantMessages(t, "close", f.s.Close(nil), oomStart+"\n"+endTrace)
	})

	t.Run("a warning report goes on after an oom kill", func(t *testing.T) {
		f := newFeeder(t)
		f.kernel(0, cutHere)

		if out := f.kernel(time.Second, oomKill); len(out) != 0 {
			t.Fatalf("oom kill gave %d outputs, want 0 inside a cut here report", len(out))
		}

		wantMessages(t, "close", f.s.Close(nil), cutHere+"\n"+oomKill)
	})
}

func TestKernel_AStartMarkerCanBeInsideTheLine(t *testing.T) {
	f := newFeeder(t)

	f.kernel(0, "[ 5.0] "+cutHere)
	f.kernel(time.Second, "[ 5.1] plain")

	wantMessages(t, "report", f.s.Close(nil), "[ 5.0] "+cutHere+"\n[ 5.1] plain")
}

func TestKernel_ASingleLineReportIsTheOriginalRecord(t *testing.T) {
	f := newFeeder(t)
	start := f.line(0, kernelProg, 17, oomStart)
	logOf(t, Output{Rec: start.Rec}).Priority = u8(4)
	f.put(start)

	out := f.s.Close(nil)
	needOutputs(t, out, 1)

	wantMessages(t, "close", out, oomStart)
	wantPriority(t, "single line", out[0], u8(4))

	if got := logOf(t, out[0]).PID; got != 17 {
		t.Errorf("PID = %d, want 17: a report of one line is the line itself", got)
	}
}

func TestKernel_APendingStartLineIsHeld(t *testing.T) {
	f := newFeeder(t)
	start := f.line(0, kernelProg, 0, oomStart)

	if out := f.put(start); len(out) != 0 {
		t.Fatalf("start line gave %d outputs, want 0", len(out))
	}

	if got, ok := f.s.HeldFrom(); !ok || got != start.Mark {
		t.Errorf("HeldFrom() = %+v, %v, want %+v, true", got, ok, start.Mark)
	}
}

func TestKernel_TheReportHasAtMost2000Lines(t *testing.T) {
	f := newFeeder(t)
	f.kernel(0, oomStart)

	for i := 2; i < 2000; i++ {
		if out := f.kernel(time.Second, "x"); len(out) != 0 {
			t.Fatalf("line %d gave %d outputs, want 0 before the limit", i, len(out))
		}
	}

	out := f.kernel(time.Second, "x")

	if len(out) != 1 {
		t.Fatalf("line 2000 gave %d outputs, want the report", len(out))
	}

	l := logOf(t, out[0])

	if got := strings.Count(l.Message, "\n") + 1; got != 2000 {
		t.Errorf("report has %d lines, want 2000", got)
	}

	if l.Truncated {
		t.Errorf("Truncated = true, want false: nothing was left out")
	}

	if f.s.Open() {
		t.Errorf("Open() = true after the limit, want false")
	}

	wantMessages(t, "line 2001 has no report", f.kernel(2*time.Second, "x"), "x")
}

func TestKernel_AStartLineEndsTheOpenReportAndOpensTheNext(t *testing.T) {
	f := newFeeder(t)
	f.kernel(0, oomStart)
	f.kernel(time.Second, "[1.1] first report")

	out := f.kernel(2*time.Second, cutHere)

	wantMessages(t, "first report", out, oomStart+"\n[1.1] first report")

	wantMessages(t, "second report", f.s.Close(nil), cutHere)
}

func TestKernel_SixtySecondsAfterTheFirstLineEndTheReport(t *testing.T) {
	cases := []struct {
		name   string
		offset time.Duration
		joins  bool
	}{
		{"60 s after", 60 * time.Second, true},
		{"60 s and 1 us after", 60*time.Second + time.Microsecond, false},
		{"same time", 0, true},
		{"30 s before, a line read late", -30 * time.Second, true},
		{"an hour before", -time.Hour, true},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newFeeder(t)
			f.kernel(0, oomStart)

			out := f.kernel(tc.offset, "[1.1] line")

			if tc.joins {
				wantMessages(t, "joined", out)
				wantMessages(t, "report", f.s.Close(nil), oomStart+"\n[1.1] line")

				return
			}

			wantMessages(t, "report, then the line", out, oomStart, "[1.1] line")
		})
	}

	t.Run("a start line after 61 s opens a new report", func(t *testing.T) {
		f := newFeeder(t)
		f.kernel(0, oomStart)

		out := f.kernel(61*time.Second, cutHere)

		wantMessages(t, "first report", out, oomStart)
		wantMessages(t, "new report", f.s.Close(nil), cutHere)
	})

	t.Run("a start line inside 60 s ends the report even when it is a start line", func(t *testing.T) {
		f := newFeeder(t)
		f.kernel(0, oomStart)

		wantMessages(t, "first report", f.kernel(30*time.Second, oomStart), oomStart)
	})
}

func TestKernel_OtherHostsAndProgramsPassAtOnce(t *testing.T) {
	f := newFeeder(t)
	f.kernel(0, oomStart)

	otherHost := f.line(time.Second, kernelProg, 0, "[9.0] line of another host")
	logOf(t, Output{Rec: otherHost.Rec}).Host = "web-2"

	otherStart := f.line(2*time.Second, kernelProg, 0, "[9.1] "+cutHere)
	logOf(t, Output{Rec: otherStart.Rec}).Host = "web-2"

	wantMessages(t, "other host", f.put(otherHost), "[9.0] line of another host")
	wantMessages(t, "start line of another host", f.put(otherStart), "[9.1] "+cutHere)
	wantMessages(t, "other program", f.add(3*time.Second, "sshd", 812, "Accepted publickey"), "Accepted publickey")
	wantMessages(t, "program with kernel in its name", f.add(4*time.Second, "kernel-helper", 5, "line"), "line")

	f.kernel(5*time.Second, "[1.1] still joins")

	wantMessages(t, "report", f.s.Close(nil), oomStart+"\n[1.1] still joins")
}

// kernelBlock returns the i-th line of a report whose lines are all 1,000 bytes.
func kernelBlock(i int) string {
	prefix := fmt.Sprintf("L%02d ", i)
	if i == 1 {
		prefix += "invoked oom-killer: "
	}

	return prefix + strings.Repeat("x", 1000-len(prefix))
}

func TestKernel_HeadAndTailOfALongReport(t *testing.T) {
	f := newFeeder(t)

	for i := 1; i <= 30; i++ {
		if out := f.kernel(time.Duration(i)*time.Millisecond, kernelBlock(i)); len(out) != 0 {
			t.Fatalf("line %d gave %d outputs, want 0", i, len(out))
		}
	}

	out := f.s.Close(nil)
	needOutputs(t, out, 1)

	// The head keeps 8 lines (8,007 of its 8,192 bytes), the tail the last 8 that fit beside it; 14 lines are left out.
	var want []string
	for i := 1; i <= 8; i++ {
		want = append(want, kernelBlock(i))
	}

	want = append(want, "[14 lines omitted]")

	for i := 23; i <= 30; i++ {
		want = append(want, kernelBlock(i))
	}

	wantMessages(t, "report", out, strings.Join(want, "\n"))

	l := logOf(t, out[0])

	if !l.Truncated {
		t.Errorf("Truncated = false, want true: lines were left out")
	}

	if len(l.Message) > model.MaxTextBytes {
		t.Errorf("message has %d bytes, want at most %d", len(l.Message), model.MaxTextBytes)
	}
}

func TestKernel_AFirstLineBeyondTheHeadIsCut(t *testing.T) {
	f := newFeeder(t)
	first := "invoked oom-killer: " + strings.Repeat("a", 9000-len("invoked oom-killer: "))

	f.kernel(0, first)
	f.kernel(time.Second, "x")

	out := f.s.Close(nil)
	needOutputs(t, out, 1)

	wantMessages(t, "report", out, first[:8192]+"\nx")

	if !logOf(t, out[0]).Truncated {
		t.Errorf("Truncated = false, want true: the first line was cut")
	}
}

func TestKernel_ALastLineThatDoesNotFitIsCut(t *testing.T) {
	f := newFeeder(t)
	first := "invoked oom-killer: " + strings.Repeat("a", 80)
	huge := strings.Repeat("b", model.MaxTextBytes)

	f.kernel(0, first)
	f.kernel(time.Second, huge)

	out := f.s.Close(nil)
	needOutputs(t, out, 1)
	l := logOf(t, out[0])

	if len(l.Message) > model.MaxTextBytes {
		t.Fatalf("message has %d bytes, want at most %d", len(l.Message), model.MaxTextBytes)
	}

	if !strings.HasPrefix(l.Message, first+"\nbbbb") || !l.Truncated {
		t.Errorf("message = %.60q... (%d bytes), truncated %v, want the first line, a cut last line and Truncated", l.Message, len(l.Message), l.Truncated)
	}
}

func TestKernel_PriorityIsTheLowestOfTheLines(t *testing.T) {
	cases := []struct {
		name  string
		first *uint8
		rest  []*uint8
		want  *uint8
	}{
		{"the kill line is the lowest", u8(4), []*uint8{u8(6), u8(3), u8(6)}, u8(3)},
		{"the first line is the lowest", u8(2), []*uint8{u8(6), u8(3)}, u8(2)},
		{"lines without a priority are ignored", nil, []*uint8{nil, u8(5), nil}, u8(5)},
		{"no line has a priority", nil, []*uint8{nil, nil}, nil},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newFeeder(t)
			first := f.line(0, kernelProg, 0, oomStart)
			logOf(t, Output{Rec: first.Rec}).Priority = tc.first
			f.put(first)

			for i, prio := range tc.rest {
				l := f.line(time.Duration(i+1)*time.Second, kernelProg, 0, fmt.Sprintf("[1.%d] line", i))
				logOf(t, Output{Rec: l.Rec}).Priority = prio
				f.put(l)
			}

			out := f.s.Close(nil)
			needOutputs(t, out, 1)

			wantPriority(t, "report", out[0], tc.want)
		})
	}
}

func TestKernel_TruncatedWhenALineWasCut(t *testing.T) {
	f := newFeeder(t)
	f.kernel(0, oomStart)

	cut := f.line(time.Second, kernelProg, 0, "[1.1] cut line")
	logOf(t, Output{Rec: cut.Rec}).Truncated = true
	f.put(cut)

	if !logOf(t, closeOne(t, f.s)).Truncated {
		t.Errorf("Truncated = false, want true")
	}
}

func TestKernel_AReportOfSeveralLinesHasPidZero(t *testing.T) {
	f := newFeeder(t)

	first := f.line(0, kernelProg, 5, oomStart)
	f.put(first)
	f.add(time.Second, kernelProg, 6, "[1.1] second")

	out := f.s.Close(nil)
	needOutputs(t, out, 1)

	if l := logOf(t, out[0]); l.PID != 0 || l.Program != kernelProg {
		t.Errorf("report = pid %d, program %q, want pid 0, program kernel", l.PID, l.Program)
	}
}
