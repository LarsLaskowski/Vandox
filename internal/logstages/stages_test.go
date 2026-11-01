package logstages

import (
	"fmt"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// base is the capture time the tests count from.
var base = time.Date(2026, 10, 31, 10, 0, 0, 0, time.UTC)

// u8 returns a pointer to a priority.
func u8(v uint8) *uint8 { return &v }

// feeder hands numbered lines to stages, the way the collector does: every line gets the next position as its mark and
// the capture time of the line before it.
type feeder struct {
	t        *testing.T
	s        *Stages
	pos      uint64
	prev     time.Time
	runStart time.Time
}

func newFeeder(t *testing.T) *feeder {
	t.Helper()

	return &feeder{t: t, s: New(), pos: 100, prev: base.Add(-time.Second), runStart: base}
}

// line builds the next line of the journal source on host web-1 with priority 6, captured at base plus at.
func (f *feeder) line(at time.Duration, program string, pid int32, msg string) Line {
	f.pos++

	return Line{
		Rec: model.Record{
			Meta: model.Meta{Origin: model.OriginAgent, Source: "journal", Seq: f.pos, CapturedAt: base.Add(at)},
			Data: &model.LogLine{Log: "journal", Host: "web-1", Program: program, PID: pid, Priority: u8(6), Message: msg},
		},
		Mark:     Mark{At: f.pos, Before: f.prev},
		RunStart: f.runStart,
	}
}

// put hands a line to the stages and returns what became ready.
func (f *feeder) put(l Line) []Output {
	f.t.Helper()

	f.prev = l.Rec.CapturedAt

	return f.s.Add(l, nil)
}

// add builds a line and hands it to the stages.
func (f *feeder) add(at time.Duration, program string, pid int32, msg string) []Output {
	f.t.Helper()

	return f.put(f.line(at, program, pid, msg))
}

// logOf returns the log line of an output.
func logOf(t *testing.T, o Output) *model.LogLine {
	t.Helper()

	l, ok := o.Rec.Data.(*model.LogLine)
	if !ok {
		t.Fatalf("output payload is %T, want *model.LogLine", o.Rec.Data)
	}

	return l
}

// messages returns the messages of the outputs.
func messages(t *testing.T, out []Output) []string {
	t.Helper()

	got := make([]string, 0, len(out))
	for _, o := range out {
		got = append(got, logOf(t, o).Message)
	}

	return got
}

// wantMessages compares the messages of the outputs with the wanted ones.
func wantMessages(t *testing.T, what string, out []Output, want ...string) {
	t.Helper()

	got := messages(t, out)
	if len(got) != len(want) {
		t.Fatalf("%s: got %d outputs %q, want %d %q", what, len(got), got, len(want), want)
	}

	for i := range want {
		if got[i] != want[i] {
			t.Errorf("%s: output %d message = %q, want %q", what, i, got[i], want[i])
		}
	}
}

func prioString(p *uint8) string {
	if p == nil {
		return "none"
	}

	return fmt.Sprint(*p)
}

// wantPriority compares the priority of an output.
func wantPriority(t *testing.T, what string, o Output, want *uint8) {
	t.Helper()

	got := logOf(t, o).Priority
	if prioString(got) != prioString(want) {
		t.Errorf("%s: priority = %s, want %s", what, prioString(got), prioString(want))
	}
}

// needOutputs stops the test when the stages gave a different number of outputs.
func needOutputs(t *testing.T, out []Output, want int) {
	t.Helper()

	if len(out) != want {
		t.Fatalf("got %d outputs %q, want %d", len(out), messages(t, out), want)
	}
}

// closeOne closes the stages and returns the one output.
func closeOne(t *testing.T, s *Stages) Output {
	t.Helper()

	out := s.Close(nil)
	needOutputs(t, out, 1)

	return out[0]
}

// header opens a MariaDB entry of the test: program mariadbd, pid 2345.
func (f *feeder) header(at time.Duration, msg string) []Output {
	f.t.Helper()

	return f.add(at, "mariadbd", 2345, msg)
}

const (
	headerNote = "2026-10-31 10:00:00 0 [Note] "
	entryStart = "Starting MariaDB 10.6.12 source revision  as process 2345"
)

func TestStages_NewIsEmpty(t *testing.T) {
	s := New()

	if s.Open() {
		t.Errorf("New().Open() = true, want false")
	}

	if m, ok := s.HeldFrom(); ok {
		t.Errorf("New().HeldFrom() = %+v, true, want no mark", m)
	}

	if out := s.Close(nil); len(out) != 0 {
		t.Errorf("New().Close(nil) = %d outputs, want 0", len(out))
	}

	if out := s.Expire(base.Add(time.Hour), nil); len(out) != 0 {
		t.Errorf("New().Expire(...) = %d outputs, want 0", len(out))
	}
}

func TestStages_AddPassesALineThroughAtOnceWithTheTimeBeforeIt(t *testing.T) {
	f := newFeeder(t)

	f.add(0, "sshd", 7, "first")
	out := f.add(5*time.Second, "sshd", 7, "second")

	wantMessages(t, "pass through", out, "second")

	if want := base; !out[0].From.Equal(want) {
		t.Errorf("From = %v, want the time of the line before, %v", out[0].From, want)
	}

	if !out[0].Rec.CapturedAt.Equal(base.Add(5 * time.Second)) {
		t.Errorf("CapturedAt = %v, want %v", out[0].Rec.CapturedAt, base.Add(5*time.Second))
	}

	if f.s.Open() {
		t.Errorf("Open() = true after a line of another program, want false")
	}
}

func TestStages_AddAppendsToTheGivenSlice(t *testing.T) {
	f := newFeeder(t)
	first := f.line(0, "sshd", 7, "first")
	second := f.line(time.Second, "sshd", 7, "second")

	out := f.s.Add(first, nil)
	out = f.s.Add(second, out)

	wantMessages(t, "append", out, "first", "second")
}

func TestStages_AddClassifiesMailLines(t *testing.T) {
	f := newFeeder(t)

	out := f.add(0, "postfix/postfix-script", 3001, "fatal: the Postfix mail system is not running")

	wantMessages(t, "postfix", out, "fatal: the Postfix mail system is not running")
	wantPriority(t, "postfix fatal", out[0], u8(2))

	if got := logOf(t, out[0]).Event; got != "postfix.fatal" {
		t.Errorf("Event = %q, want %q", got, "postfix.fatal")
	}
}

func TestStages_MailOnlyLineGetsTheMailClassificationAndLeavesTheStagesAlone(t *testing.T) {
	f := newFeeder(t)

	f.header(0, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345")
	f.add(time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")

	mail := f.line(2*time.Second, "dovecot", 0, "auth: Fatal: master: service(auth): child 2290 returned error 89")
	mail.MailOnly = true
	out := f.put(mail)

	wantMessages(t, "mail only", out, "auth: Fatal: master: service(auth): child 2290 returned error 89")

	if got := logOf(t, out[0]).Event; got != "dovecot.fatal" {
		t.Errorf("Event = %q, want %q", got, "dovecot.fatal")
	}

	wantPriority(t, "dovecot fatal", out[0], u8(2))

	sameKey := f.line(3*time.Second, "mariadbd", 2345, "continuation of the entry")
	sameKey.MailOnly = true
	out = f.put(sameKey)

	wantMessages(t, "same key, mail only", out, "continuation of the entry")

	other := f.line(4*time.Second, "mariadbd", 2345, "2026-10-31 10:00:04 0 [Note] a header that must not close the entry")
	other.MailOnly = true
	out = f.put(other)

	wantMessages(t, "header, mail only", out, "2026-10-31 10:00:04 0 [Note] a header that must not close the entry")

	closed := f.s.Close(nil)

	wantMessages(t, "close", closed, "Starting MariaDB 10.6.12 source revision  as process 2345", "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
}

func TestStages_MailOnlyLinesOpenNothing(t *testing.T) {
	f := newFeeder(t)

	header := f.line(0, "mariadbd", 2345, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345")
	header.MailOnly = true
	start := f.line(time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
	start.MailOnly = true

	if out := f.put(header); len(out) != 1 {
		t.Errorf("mail-only header gave %d outputs, want 1 at once", len(out))
	}

	if out := f.put(start); len(out) != 1 {
		t.Errorf("mail-only kernel start gave %d outputs, want 1 at once", len(out))
	}

	if f.s.Open() {
		t.Errorf("Open() = true after mail-only lines, want false")
	}

	if _, ok := f.s.HeldFrom(); ok {
		t.Errorf("HeldFrom() holds a line after mail-only lines, want none")
	}
}

func TestStages_OpenAndCloseFollowTheGroups(t *testing.T) {
	f := newFeeder(t)

	if out := f.header(0, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345"); len(out) != 0 {
		t.Fatalf("a header gave %d outputs, want 0 while the entry is open", len(out))
	}

	if !f.s.Open() {
		t.Errorf("Open() = false with an open entry, want true")
	}

	if out := f.add(time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca"); len(out) != 0 {
		t.Fatalf("a kernel start gave %d outputs, want 0 while the report is open", len(out))
	}

	closed := f.s.Close(nil)

	wantMessages(t, "close order: entry, then report", closed, "Starting MariaDB 10.6.12 source revision  as process 2345", "[1.0] example invoked oom-killer: gfp_mask=0x100cca")

	if f.s.Open() {
		t.Errorf("Open() = true after Close, want false")
	}

	if again := f.s.Close(nil); len(again) != 0 {
		t.Errorf("a second Close gave %d outputs, want 0", len(again))
	}
}

func TestStages_AnEmittedGroupPassesTheLaterStagesInOrder(t *testing.T) {
	f := newFeeder(t)

	f.header(0, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345")
	f.add(time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")

	out := f.header(2*time.Second, headerNote+"/usr/sbin/mariadbd: ready for connections.")

	wantMessages(t, "next header", out, "Starting MariaDB 10.6.12 source revision  as process 2345")

	if got := logOf(t, out[0]).Event; got != "mariadb.start" {
		t.Errorf("Event = %q, want mariadb.start", got)
	}

	if !f.s.Open() {
		t.Errorf("Open() = false, want true: the new entry and the report are open")
	}
}

func TestStages_OutputFromIsTheTimeBeforeTheFirstLineOfTheGroup(t *testing.T) {
	f := newFeeder(t)

	f.add(0, "sshd", 7, "before the entry")
	f.header(10*time.Second, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345")
	f.add(11*time.Second, "mariadbd", 2345, "continuation")

	out := f.add(12*time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
	if len(out) != 0 {
		t.Fatalf("kernel start gave %d outputs, want 0", len(out))
	}

	closed := f.s.Close(nil)
	if len(closed) != 2 {
		t.Fatalf("Close gave %d outputs, want 2", len(closed))
	}

	if want := base; !closed[0].From.Equal(want) {
		t.Errorf("entry From = %v, want the time of the line before its header, %v", closed[0].From, want)
	}

	if want := base.Add(11 * time.Second); !closed[1].From.Equal(want) {
		t.Errorf("report From = %v, want the time of the line before its first line, %v", closed[1].From, want)
	}
}

func TestStages_Expire(t *testing.T) {
	for _, tc := range []struct {
		name string
		run  func(*testing.T)
	}{
		{"entry closes only when t lies more than MaxSpan after its first line", expireEntryAfterMaxSpan},
		{"report closes only when t lies more than MaxSpan after its first line", expireReportAfterMaxSpan},
		{"a group dated in the future is anchored at the start of its run", expireEntryDatedInTheFuture},
		{"a report dated in the future is anchored at the start of its run", expireReportDatedInTheFuture},
		{"a group read long after its capture time is anchored at its capture time", expireGroupReadLate},
		{"only the groups past their anchor close, the entry first", expireOnlyGroupsPastTheirAnchor},
		{"expiry appends and carries the start of the replacing gap", expireAppendsAndCarriesFrom},
	} {
		t.Run(tc.name, tc.run)
	}
}

func expireEntryAfterMaxSpan(t *testing.T) {
	f := newFeeder(t)
	f.header(0, headerNote+entryStart)
	f.add(30*time.Second, "mariadbd", 2345, "a later line does not move the anchor")

	if out := f.s.Expire(base.Add(MaxSpan), nil); len(out) != 0 {
		t.Fatalf("Expire at anchor + MaxSpan gave %d outputs, want 0", len(out))
	}

	out := f.s.Expire(base.Add(MaxSpan+time.Microsecond), nil)
	wantMessages(t, "expire at anchor + MaxSpan + 1us", out, entryStart+"\na later line does not move the anchor")

	if f.s.Open() {
		t.Errorf("Open() = true after the entry expired, want false")
	}
}

func expireReportAfterMaxSpan(t *testing.T) {
	f := newFeeder(t)
	f.add(0, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")

	if out := f.s.Expire(base.Add(MaxSpan), nil); len(out) != 0 {
		t.Fatalf("Expire at anchor + MaxSpan gave %d outputs, want 0", len(out))
	}

	out := f.s.Expire(base.Add(MaxSpan+time.Microsecond), nil)
	wantMessages(t, "expire at anchor + MaxSpan + 1us", out, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
}

func expireEntryDatedInTheFuture(t *testing.T) {
	f := newFeeder(t)
	f.header(365*24*time.Hour, headerNote+entryStart)

	if out := f.s.Expire(base.Add(MaxSpan), nil); len(out) != 0 {
		t.Fatalf("Expire at run start + MaxSpan gave %d outputs, want 0", len(out))
	}

	out := f.s.Expire(base.Add(MaxSpan+time.Microsecond), nil)
	wantMessages(t, "expire at run start + MaxSpan + 1us", out, entryStart)
}

func expireReportDatedInTheFuture(t *testing.T) {
	f := newFeeder(t)
	f.add(365*24*time.Hour, "kernel", 0, "------------[ cut here ]------------")

	if out := f.s.Expire(base.Add(MaxSpan), nil); len(out) != 0 {
		t.Fatalf("Expire at run start + MaxSpan gave %d outputs, want 0", len(out))
	}

	out := f.s.Expire(base.Add(MaxSpan+time.Microsecond), nil)
	wantMessages(t, "expire after the run start", out, "------------[ cut here ]------------")
}

func expireGroupReadLate(t *testing.T) {
	f := newFeeder(t)
	f.runStart = base.Add(time.Hour)
	f.header(0, headerNote+entryStart)

	if out := f.s.Expire(base.Add(MaxSpan), nil); len(out) != 0 {
		t.Fatalf("Expire at capture time + MaxSpan gave %d outputs, want 0", len(out))
	}

	out := f.s.Expire(base.Add(MaxSpan+time.Microsecond), nil)
	wantMessages(t, "expire after the capture time", out, entryStart)
}

func expireOnlyGroupsPastTheirAnchor(t *testing.T) {
	f := newFeeder(t)
	f.header(0, headerNote+entryStart)

	// The report is read in a run that starts when its first line is captured, so its anchor is its capture time.
	f.runStart = base.Add(50 * time.Second)
	f.add(50*time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")

	out := f.s.Expire(base.Add(61*time.Second), nil)
	wantMessages(t, "entry only", out, entryStart)

	if !f.s.Open() {
		t.Errorf("Open() = false, want true: the report is still open")
	}

	out = f.s.Expire(base.Add(111*time.Second), nil)
	wantMessages(t, "report", out, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")

	f.runStart = base.Add(200 * time.Second)
	f.header(200*time.Second, headerNote+entryStart)
	f.runStart = base.Add(205 * time.Second)
	f.add(205*time.Second, "kernel", 0, "[2.0] example invoked oom-killer: gfp_mask=0x100cca")

	both := f.s.Expire(base.Add(300*time.Second), nil)
	wantMessages(t, "both expire: entry, then report", both, entryStart, "[2.0] example invoked oom-killer: gfp_mask=0x100cca")
}

func expireAppendsAndCarriesFrom(t *testing.T) {
	f := newFeeder(t)
	f.add(0, "sshd", 7, "before")
	f.header(10*time.Second, headerNote+entryStart)

	out := f.s.Expire(base.Add(time.Hour), []Output{{}})
	if len(out) != 2 {
		t.Fatalf("Expire appended %d outputs to a one-element slice, want 2 in all", len(out))
	}

	if !out[1].From.Equal(base) {
		t.Errorf("From = %v, want the time before the header, %v", out[1].From, base)
	}
}

func TestStages_HeldFrom(t *testing.T) {
	for _, tc := range []struct {
		name string
		run  func(*testing.T)
	}{
		{"an entry holds from its header and a joined line does not move it", heldFromAnEntry},
		{"a report holds from its first line", heldFromAReport},
		{"the oldest of two groups wins, and the other one stays after the first closes", heldFromTheOldestGroup},
		{"the renamed flag of the mark is kept", heldFromKeepsTheRenamedFlag},
	} {
		t.Run(tc.name, tc.run)
	}
}

func heldFromAnEntry(t *testing.T) {
	f := newFeeder(t)
	f.add(0, "sshd", 7, "not held")

	header := f.line(time.Second, "mariadbd", 2345, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345")
	f.put(header)
	f.add(2*time.Second, "mariadbd", 2345, "joined")
	f.add(3*time.Second, "sshd", 7, "passes while the entry is open")

	got, ok := f.s.HeldFrom()
	if !ok {
		t.Fatalf("HeldFrom() = false, want the header's mark")
	}

	if got != header.Mark {
		t.Errorf("HeldFrom() = %+v, want %+v", got, header.Mark)
	}

	if !got.Before.Equal(base) {
		t.Errorf("Mark.Before = %v, want the time of the line before the header, %v", got.Before, base)
	}
}

func heldFromAReport(t *testing.T) {
	f := newFeeder(t)
	start := f.line(0, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
	f.put(start)
	f.add(time.Second, "kernel", 0, "[1.1] CPU: 1 PID: 4242 Comm: example")

	got, ok := f.s.HeldFrom()
	if !ok || got != start.Mark {
		t.Errorf("HeldFrom() = %+v, %v, want %+v, true", got, ok, start.Mark)
	}
}

func heldFromTheOldestGroup(t *testing.T) {
	f := newFeeder(t)
	start := f.line(0, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
	f.put(start)
	f.runStart = base.Add(10 * time.Second)
	header := f.line(10*time.Second, "mariadbd", 2345, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345")
	f.put(header)

	got, ok := f.s.HeldFrom()
	if !ok || got != start.Mark {
		t.Fatalf("HeldFrom() = %+v, %v, want the report's %+v", got, ok, start.Mark)
	}

	f.s.Expire(base.Add(MaxSpan+time.Second), nil)

	got, ok = f.s.HeldFrom()
	if !ok || got != header.Mark {
		t.Errorf("HeldFrom() after the report expired = %+v, %v, want the entry's %+v", got, ok, header.Mark)
	}

	f.s.Close(nil)

	if got, ok = f.s.HeldFrom(); ok {
		t.Errorf("HeldFrom() after Close = %+v, true, want none", got)
	}
}

func heldFromKeepsTheRenamedFlag(t *testing.T) {
	f := newFeeder(t)
	start := f.line(0, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
	start.Mark.Renamed = true
	f.put(start)

	got, ok := f.s.HeldFrom()
	if !ok || !got.Renamed || got.At != start.Mark.At {
		t.Errorf("HeldFrom() = %+v, %v, want the renamed mark %+v", got, ok, start.Mark)
	}
}

func TestStages_CloneIsDeep(t *testing.T) {
	for _, tc := range []struct {
		name string
		run  func(*testing.T)
	}{
		{"lines added to the clone do not reach the original and the other way round", cloneLinesDoNotCross},
		{"closing the clone leaves the original open", cloneCloseLeavesTheOriginalOpen},
		{"a held mark is copied", cloneCopiesTheHeldMark},
		{"the recovery state is copied and then independent", cloneCopiesTheRecoveryState},
		{"an empty clone is empty", cloneOfEmptyStages},
	} {
		t.Run(tc.name, tc.run)
	}
}

func cloneLinesDoNotCross(t *testing.T) {
	f := newFeeder(t)
	f.header(0, headerNote+entryStart)
	f.add(time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")

	clone := f.s.Clone()

	// The clone joins a line to the entry and the report.
	extra := f.line(2*time.Second, "mariadbd", 2345, "joined in the clone")
	extraKernel := f.line(2*time.Second, "kernel", 0, "[1.1] joined in the clone")
	clone.Add(extra, nil)
	clone.Add(extraKernel, nil)

	// The original joins other lines.
	f.add(3*time.Second, "mariadbd", 2345, "joined in the original")
	f.add(3*time.Second, "kernel", 0, "[1.2] joined in the original")

	original := f.s.Close(nil)
	cloned := clone.Close(nil)

	wantMessages(t, "original", original, entryStart+"\njoined in the original", "[1.0] example invoked oom-killer: gfp_mask=0x100cca\n[1.2] joined in the original")
	wantMessages(t, "clone", cloned, entryStart+"\njoined in the clone", "[1.0] example invoked oom-killer: gfp_mask=0x100cca\n[1.1] joined in the clone")
}

func cloneCloseLeavesTheOriginalOpen(t *testing.T) {
	f := newFeeder(t)
	f.header(0, headerNote+entryStart)

	clone := f.s.Clone()
	clone.Close(nil)

	if !f.s.Open() {
		t.Errorf("original Open() = false after the clone closed, want true")
	}

	if clone.Open() {
		t.Errorf("clone Open() = true after Close, want false")
	}

	wantMessages(t, "original close", f.s.Close(nil), entryStart)
}

func cloneCopiesTheHeldMark(t *testing.T) {
	f := newFeeder(t)
	header := f.line(0, "mariadbd", 2345, headerNote+entryStart)
	f.put(header)

	clone := f.s.Clone()
	got, ok := clone.HeldFrom()

	if !ok || got != header.Mark {
		t.Errorf("clone HeldFrom() = %+v, %v, want %+v, true", got, ok, header.Mark)
	}
}

func cloneCopiesTheRecoveryState(t *testing.T) {
	f := newFeeder(t)
	f.header(0, headerNote+"InnoDB: Starting crash recovery from checkpoint LSN=8401234,8401234")

	clone := f.s.Clone()
	ended := "2026-10-31 10:00:05 0 [Note] InnoDB: 10.6.12 started; log sequence number 8409876; transaction id 5678"

	first := f.line(5*time.Second, "mariadbd", 2345, ended)
	second := f.line(6*time.Second, "mariadbd", 2345, ended)

	closedOriginal := f.s.Close(f.s.Add(first, nil))
	closedClone := clone.Close(clone.Add(second, nil))

	if len(closedOriginal) != 2 || len(closedClone) != 2 {
		t.Fatalf("got %d and %d outputs, want 2 and 2", len(closedOriginal), len(closedClone))
	}

	if got := logOf(t, closedOriginal[1]).Event; got != "mariadb.recovery_end" {
		t.Errorf("original: Event = %q, want mariadb.recovery_end", got)
	}

	if got := logOf(t, closedClone[1]).Event; got != "mariadb.recovery_end" {
		t.Errorf("clone: Event = %q, want mariadb.recovery_end", got)
	}
}

func cloneOfEmptyStages(t *testing.T) {
	clone := New().Clone()

	if clone == nil || clone.Open() {
		t.Errorf("Clone of empty stages = %v, want empty stages", clone)
	}
}

// retainedHeap returns the live heap after a collection.
func retainedHeap() uint64 {
	var m runtime.MemStats

	runtime.GC()
	runtime.ReadMemStats(&m)

	return m.HeapAlloc
}

func TestStages_BuffersExistOnlyWhileAGroupIsOpen(t *testing.T) {
	const (
		count    = 1000
		perStage = 2048
	)

	long := strings.Repeat("x", 4000)
	all := make([]*Stages, 0, count)
	before := retainedHeap()

	for i := 0; i < count; i++ {
		f := newFeeder(t)
		f.add(0, "sshd", 7, "an unrelated line")
		f.header(time.Second, headerNote+"Starting MariaDB 10.6.12 source revision  as process 2345")
		f.add(2*time.Second, "mariadbd", 2345, long)
		f.add(3*time.Second, "kernel", 0, "[1.0] example invoked oom-killer: gfp_mask=0x100cca")
		f.add(4*time.Second, "kernel", 0, long)

		if !f.s.Open() {
			t.Fatalf("Open() = false with an entry and a report, want true")
		}

		if out := f.s.Close(nil); len(out) != 2 {
			t.Fatalf("Close gave %d outputs, want 2", len(out))
		}

		all = append(all, f.s)
	}

	after := retainedHeap()

	if after > before && after-before > count*perStage {
		t.Errorf("%d stages whose groups closed retain %d bytes, want at most %d (%d per stage)", count, after-before, count*perStage, perStage)
	}

	runtime.KeepAlive(all)
}
