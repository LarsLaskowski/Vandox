package mariadb

import (
	"bytes"
	"cmp"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"math/rand/v2"
	"slices"
	"strings"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const processlistStep = "processlist"

// statusRow is a row of SHOW GLOBAL STATUS as selectStatus is fed.
type statusRow struct {
	name, value string
	cut         bool
}

// statusRows returns the row source of selectStatus for rows.
func statusRows(rows []statusRow) func(func(name, value []byte, cut bool) error) error {
	return func(yield func(name, value []byte, cut bool) error) error {
		for _, r := range rows {
			if err := yield([]byte(r.name), []byte(r.value), r.cut); err != nil {
				return err
			}
		}
		return nil
	}
}

func TestSelection_LeavesOutUnusableValues(t *testing.T) {
	for _, tc := range []struct {
		name string
		rows []statusRow
		want map[string]uint64
	}{
		{"plain values", []statusRow{{"Uptime", "5", false}, {"Questions", "18446744073709551615", false}, {"Slow_queries", "0", false}}, map[string]uint64{"Uptime": 5, "Questions": 18446744073709551615, "Slow_queries": 0}},
		{"non-numeric values", []statusRow{
			{"Uptime", "abc", false}, {"Questions", "OFF", false}, {"Threads_running", "1.5", false}, {"Slow_queries", "-1", false},
			{"Memory_used", "+1", false}, {"Aborted_clients", "", false}, {"Max_used_connections", " 1", false},
		}, map[string]uint64{}},
		{"more than 20 digits", []statusRow{{"Uptime", "000000000000000000001", false}, {"Questions", "100000000000000000000", false}}, map[string]uint64{}},
		{"above the largest uint64", []statusRow{{"Uptime", "18446744073709551616", false}, {"Questions", "99999999999999999999", false}}, map[string]uint64{}},
		{"names outside the selection", []statusRow{{"Bytes_sent", "5", false}, {"Com_select", "7", false}, {"Uptime", "9", false}}, map[string]uint64{"Uptime": 9}},
		{"the whole selection", wholeSelection(), wholeSelectionWant()},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := selectStatus(statusRows(tc.rows))

			if err != nil {
				t.Fatalf("selectStatus() = %v, want the usable values", err)
			}
			if !mapsEqual(got, tc.want) {
				t.Errorf("selectStatus() = %v, want %v", got, tc.want)
			}
		})
	}
}

// wholeSelection returns one usable row per status name of the selection, among rows of other names.
func wholeSelection() []statusRow {
	rows := []statusRow{{"Aaa_first", "1", false}}
	for i, name := range selectedStatus {
		rows = append(rows, statusRow{name, fmt.Sprint(i + 10), false}, statusRow{"Other_" + name, "3", false})
	}
	return rows
}

// wholeSelectionWant is what selectStatus keeps of wholeSelection.
func wholeSelectionWant() map[string]uint64 {
	want := map[string]uint64{}
	for i, name := range selectedStatus {
		want[name] = uint64(i + 10)
	}
	return want
}

func TestSelection_StatusNameSentTwiceIsAnError(t *testing.T) {
	rows := []statusRow{{"Uptime", "1", false}, {"Bytes_sent", "2", false}, {"Questions", "3", false}, {"Uptime", "4", false}}

	got, err := selectStatus(statusRows(rows))

	wantError(t, "selectStatus()", err, "mariadb: status: name sent twice")
	if len(got) != 0 {
		t.Errorf("selectStatus() = %v with the error, want no values", got)
	}
}

func TestSelection_OtherNamesMayRepeat(t *testing.T) {
	rows := []statusRow{{"Bytes_sent", "1", false}, {"Bytes_sent", "2", false}, {"Uptime", "4", false}}

	got, err := selectStatus(statusRows(rows))

	if err != nil || !mapsEqual(got, map[string]uint64{"Uptime": 4}) {
		t.Errorf("selectStatus() = %v, %v, want only Uptime: names outside the selection are neither kept nor remembered", got, err)
	}
}

func TestSelection_StopsAtTheErrorOfTheRows(t *testing.T) {
	stop := errors.New("stop here")
	rows := func(yield func(name, value []byte, cut bool) error) error {
		if err := yield([]byte("Uptime"), []byte("1"), false); err != nil {
			return err
		}
		return stop
	}

	_, err := selectStatus(rows)

	if !errors.Is(err, stop) {
		t.Errorf("selectStatus() error = %v, want the error of the rows", err)
	}
}

func TestSelection_KeepsVariablesWithinTheirBounds(t *testing.T) {
	invalid := func(n int) string { return strings.Repeat(string([]byte{0xff}), n) }
	replacement := string(rune(0xfffd))
	for _, tc := range []struct {
		name  string
		value string
		cut   bool
		want  string
		kept  bool
	}{
		{"a plain value", "ON", false, "ON", true},
		{"an empty value", "", false, "", true},
		{"1,024 bytes", strings.Repeat("a", 1024), false, strings.Repeat("a", 1024), true},
		{"1,025 bytes", strings.Repeat("a", 1025), false, "", false},
		{"a cut value", "aaaa", true, "", false},
		{"512 two-byte characters", strings.Repeat("é", 512), false, strings.Repeat("é", 512), true},
		{"341 invalid bytes clean to 1,023", invalid(341), false, strings.Repeat(replacement, 341), true},
		{"342 invalid bytes clean to 1,026", invalid(342), false, "", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, kept := keepVariable([]byte(tc.value), tc.cut)

			if got != tc.want || kept != tc.kept {
				t.Errorf("keepVariable(%d bytes, cut %v) = %d bytes, %v, want %d bytes, %v", len(tc.value), tc.cut, len(got), kept, len(tc.want), tc.kept)
			}
		})
	}
}

// visitRows feeds rows to a thread list the way the client does: a packet per row, each started by beginRow.
func visitRows(t *testing.T, l *threadList, rows [][]textCell) error {
	t.Helper()
	payloads := make([][]byte, len(rows))
	for i, r := range rows {
		payloads[i] = rowPayload(r...)
	}
	p := newPacketReader(bytes.NewReader(frames(payloads...)))
	for range rows {
		if _, err := p.beginRow(processlistStep); err != nil {
			t.Fatalf("beginRow() = %v", err)
		}
		if err := l.visit(p, processlistStep); err != nil {
			return err
		}
	}
	return nil
}

func TestThreads_OrderAndInfoCut(t *testing.T) {
	l := newThreadList(22128)

	if err := visitRows(t, l, threadRows("22128")); err != nil {
		t.Fatalf("visit() = %v, want the rows", err)
	}

	if got, want := l.threads(), expectedThreads(); !slices.Equal(got, want) {
		t.Errorf("threads() = %+v\nwant %+v: working threads first, then sleeping ones, each by time descending and id, the agent's own left out, a statement cut to 16 KiB", got, want)
	}
}

func TestThreads_InfoOfExactlyTheLimitIsNotTruncated(t *testing.T) {
	l := newThreadList(1)
	rows := [][]textCell{processlistRow("2", "u", "h", "d", "Query", "1", "s", strings.Repeat("y", 16384), "0.000")}

	if err := visitRows(t, l, rows); err != nil {
		t.Fatalf("visit() = %v", err)
	}

	got := l.threads()
	if len(got) != 1 || len(got[0].Info) != 16384 || got[0].Truncated {
		t.Errorf("threads() = %d threads, info %d bytes, truncated %v, want one thread with 16384 bytes and no truncation", len(got), len(got[0].Info), got[0].Truncated)
	}
}

func TestThreads_ShortFieldsAreCutAtTheirCaps(t *testing.T) {
	l := newThreadList(1)
	long := func(n int) string { return strings.Repeat("z", n) }
	rows := [][]textCell{processlistRow("2", long(600), long(600), long(300), long(40), "1", long(300), "q", "0.000")}

	if err := visitRows(t, l, rows); err != nil {
		t.Fatalf("visit() = %v", err)
	}

	got := l.threads()
	if len(got) != 1 {
		t.Fatalf("threads() = %d threads, want 1", len(got))
	}
	th := got[0]
	if len(th.User) != 512 || len(th.Host) != 512 || len(th.DB) != 256 || len(th.Command) != 32 || len(th.State) != 256 {
		t.Errorf("lengths user %d, host %d, db %d, command %d, state %d, want 512, 512, 256, 32, 256", len(th.User), len(th.Host), len(th.DB), len(th.Command), len(th.State))
	}
}

func TestThreads_TimeThatIsNotDigitsReadsZero(t *testing.T) {
	for _, tc := range []struct {
		seconds string
		want    uint64
	}{
		{"7", 7}, {"18446744073709551615", 18446744073709551615}, {"abc", 0}, {"", 0}, {"-5", 0}, {"12x", 0}, {"1.5", 0},
	} {
		t.Run(fmt.Sprintf("%q", tc.seconds), func(t *testing.T) {
			l := newThreadList(1)

			if err := visitRows(t, l, [][]textCell{processlistRow("2", "u", "h", "d", "Query", tc.seconds, "s", "q", "0.000")}); err != nil {
				t.Fatalf("visit() = %v", err)
			}

			if got := l.threads(); len(got) != 1 || got[0].TimeSeconds != tc.want {
				t.Errorf("threads() = %+v, want one thread with %d seconds", got, tc.want)
			}
		})
	}
}

func TestThreads_IdMustBeADecimalNumber(t *testing.T) {
	for _, tc := range []struct {
		name string
		id   textCell
		want string
	}{
		{"letters", text("abc"), "mariadb: processlist: unexpected reply"},
		{"empty", text(""), "mariadb: processlist: unexpected reply"},
		{"sign", text("-1"), "mariadb: processlist: unexpected reply"},
		{"trailing space", text("1 "), "mariadb: processlist: unexpected reply"},
		{"above the largest uint64", text("18446744073709551616"), "mariadb: processlist: unexpected reply"},
		{"NULL", nullCell, "mariadb: processlist: unexpected reply"},
		{"21 digits", text("000000000000000000001"), "mariadb: processlist: cell too long"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			l := newThreadList(1)
			row := append([]textCell{tc.id}, processlistRow("2", "u", "h", "d", "Query", "1", "s", "q", "0.000")[1:]...)

			err := visitRows(t, l, [][]textCell{row})

			wantError(t, "visit()", err, tc.want)
		})
	}
	t.Run("the largest uint64", func(t *testing.T) {
		l := newThreadList(1)

		err := visitRows(t, l, [][]textCell{processlistRow("18446744073709551615", "u", "h", "d", "Query", "1", "s", "q", "0.000")})

		if got := l.threads(); err != nil || len(got) != 1 || got[0].ID != 18446744073709551615 {
			t.Errorf("visit() = %v, threads %+v, want the thread with the largest id", err, got)
		}
	})
}

// bulkRow describes a row of the 4,200-row process list.
type bulkRow struct {
	id      uint64
	sleep   bool
	seconds uint64
	info    string
}

// bulkRows returns n rows in a fixed random order, ties in time and sleeping threads among them, and the agent's own row.
func bulkRows(n int, own uint64) ([]bulkRow, [][]textCell) {
	rng := rand.New(rand.NewPCG(1, 2))
	rows := make([]bulkRow, 0, n)
	for i := 1; i <= n; i++ {
		info := fmt.Sprintf("statement %d", i)
		if i%5 == 0 {
			info = ""
		}
		rows = append(rows, bulkRow{id: uint64(i), sleep: i%3 == 0, seconds: uint64(i * 37 % 500), info: info})
	}
	cells := make([][]textCell, 0, n+1)
	cells = append(cells, processlistRow(fmt.Sprint(own), User, "localhost", "", "Query", "0", "starting", "SHOW FULL PROCESSLIST", "0.000"))
	for _, i := range rng.Perm(n) {
		r := rows[i]
		command := "Query"
		if r.sleep {
			command = "Sleep"
		}
		cells = append(cells, processlistRow(fmt.Sprint(r.id), "user", "host", "db", command, fmt.Sprint(r.seconds), "state", r.info, "0.000"))
	}
	rng.Shuffle(len(cells), func(i, j int) { cells[i], cells[j] = cells[j], cells[i] })
	return rows, cells
}

func TestThreads_KeepsTheFirst4096AndInfoOfTheFirst64(t *testing.T) {
	rows, cells := bulkRows(4200, 99999)
	order := slices.Clone(rows)
	slices.SortFunc(order, func(a, b bulkRow) int {
		switch {
		case a.sleep != b.sleep && a.sleep:
			return 1
		case a.sleep != b.sleep:
			return -1
		case a.seconds != b.seconds:
			return cmp.Compare(b.seconds, a.seconds)
		}
		return cmp.Compare(a.id, b.id)
	})
	l := newThreadList(99999)

	if err := visitRows(t, l, cells); err != nil {
		t.Fatalf("visit() = %v", err)
	}

	got := l.threads()
	if len(got) != 4096 {
		t.Fatalf("threads() = %d threads, want the first 4096 of the order", len(got))
	}
	for i, th := range got {
		want := order[i]
		wantInfo, wantCut := want.info, false
		if i >= 64 {
			wantInfo, wantCut = "", want.info != ""
		}
		if th.ID != want.id || th.TimeSeconds != want.seconds || th.Info != wantInfo || th.Truncated != wantCut {
			t.Fatalf("thread %d = id %d, %d seconds, info %q, truncated %v, want id %d, %d seconds, info %q, truncated %v",
				i, th.ID, th.TimeSeconds, th.Info, th.Truncated, want.id, want.seconds, wantInfo, wantCut)
		}
	}
}

func TestThreads_TextIsCleanedAndNeverLogged(t *testing.T) {
	control, format, bad, replacement := string([]byte{7}), string(rune(0x202e)), string([]byte{0xff}), string(rune(0xfffd))
	secret := "SECRETSTATEMENT"
	infoIn := "SELECT '" + secret + "'" + control + format + bad + "tail"
	infoWant := "SELECT '" + secret + "'" + control + format + replacement + "tail"
	rows := [][]textCell{
		processlistRow("10", "us"+control+"er"+bad, "ho"+format+"st", "db"+bad, "Query", "9", "sta"+control+"te", infoIn, "0.000"),
		processlistRow("11", strings.Repeat(bad, 400), "h", "d", "Query", "8", "s", strings.Repeat(bad, 10000), "0.000"),
	}
	synctest.Test(t, func(t *testing.T) {
		r := newRig(t, fakeConfig{})
		r.setStatus(map[string]string{"Threads_running": "11"})
		r.setProcesslist(rows...)

		s := statusOf(t, r.mustCollect())

		want := []model.MariaDBThread{
			{ID: 10, User: "us" + control + "er" + replacement, Host: "ho" + format + "st", DB: "db" + replacement, Command: "Query", TimeSeconds: 9, State: "sta" + control + "te", Info: infoWant},
			{ID: 11, User: strings.Repeat(replacement, 341), Host: "h", DB: "d", Command: "Query", TimeSeconds: 8, State: "s", Info: strings.Repeat(replacement, 5461), Truncated: true},
		}
		if !slices.Equal(s.Threads, want) {
			t.Errorf("threads = %+v\nwant %+v: invalid bytes as U+FFFD, control and format characters kept, texts cut to their limits at a character boundary", s.Threads, want)
		}
		checkLogs(t, "a run with a hostile process list", r.logs.take())

		r.setProcesslist(processlistRow("abc", "SECRETUSER", "h", "d", "Query", "1", "s", secret, "0.000"))
		time.Sleep(30 * time.Second)
		_, err := r.collect()
		wantError(t, "Collect()", err, "mariadb: processlist: unexpected reply")
		for _, l := range r.logs.take() {
			if line := describeLog(l.level, l.msg, l.attrs); strings.Contains(line, "SECRET") {
				t.Errorf("the failed run logged %s, text of a thread", line)
			}
		}
	})
}

// fatThreads returns a status with n threads whose statements have infoLen bytes of '<'; the first thread has a short statement
// unless infoLen is 0, which leaves every statement empty and, as the reduction does, truncated.
func fatThreads(n uint64, infoLen int, truncated bool) *model.MariaDBStatus {
	s := &model.MariaDBStatus{Availability: model.MariaDBUp, Status: map[string]uint64{"Uptime": 5}, Variables: map[string]string{"version": "12.3.3"}}
	for i := range n {
		th := model.MariaDBThread{
			ID: i + 1, User: "user", Host: "host", DB: "db", Command: "Query", TimeSeconds: n - i, State: "state",
			Info: strings.Repeat("<", infoLen), Truncated: truncated,
		}
		switch {
		case infoLen == 0:
			th.Info = ""
		case i == 0:
			th.Info, th.Truncated = "short", false
		}
		s.Threads = append(s.Threads, th)
	}
	return s
}

// checkInfos fails unless every statement but the short first one has want bytes and the truncated flag as wantCut says; the short
// first one stays as it is unless want is 0.
func checkInfos(t *testing.T, what string, s *model.MariaDBStatus, count, want int, wantCut bool) {
	t.Helper()
	if len(s.Threads) != count {
		t.Errorf("%s: %d threads, want %d", what, len(s.Threads), count)
		return
	}
	wantFirst, firstCut := "short", false
	if want == 0 {
		wantFirst, firstCut = "", true
	}
	if s.Threads[0].Info != wantFirst || s.Threads[0].Truncated != firstCut {
		t.Errorf("%s: the short statement is %q, truncated %v, want %q and %v", what, s.Threads[0].Info, s.Threads[0].Truncated, wantFirst, firstCut)
	}
	for i, th := range s.Threads[1:] {
		if len(th.Info) != want || th.Truncated != wantCut || th.ID != uint64(i+2) {
			t.Errorf("%s: thread %d has id %d, %d info bytes, truncated %v, want id %d, %d bytes, truncated %v", what, i+2, th.ID, len(th.Info), th.Truncated, i+2, want, wantCut)
			return
		}
	}
}

func TestThreads_ReducesUntilTheRecordFits(t *testing.T) {
	const n = 16
	for _, tc := range []struct {
		name    string
		limit   int // the estimate of the state the reduction must stop at, less delta
		delta   int
		count   int
		info    int
		wantCut bool
	}{
		{"fits already", estimate(fatThreads(n, 16384, false)), 0, n, 16384, false},
		{"statements cut to 4,096 bytes", estimate(fatThreads(n, 4096, true)), 0, n, 4096, true},
		{"statements cut to 1,024 bytes", estimate(fatThreads(n, 1024, true)), 0, n, 1024, true},
		{"statements dropped", estimate(fatThreads(n, 0, true)), 0, n, 0, true},
		{"threads halved from the end", estimate(fatThreads(n, 0, true)), 1, n / 2, 0, true},
		{"threads halved twice", estimate(fatThreads(n/2, 0, true)), 1, n / 4, 0, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := fatThreads(n, 16384, false)
			want := slices.Clone(s.Threads)
			limit := tc.limit - tc.delta
			if limit <= 0 {
				t.Fatalf("the reference estimate is %d, want a positive limit: estimate() is a stub", tc.limit)
			}

			fitThreads(s, limit)

			checkInfos(t, "after fitThreads", s, tc.count, tc.info, tc.wantCut)
			if got := estimate(s); got > limit {
				t.Errorf("estimate() = %d after fitThreads(%d), want at most the limit", got, limit)
			}
			for i, th := range s.Threads {
				if th.ID != want[i].ID || th.User != want[i].User {
					t.Fatalf("thread %d = id %d, want id %d: threads are left out from the end only", i, th.ID, want[i].ID)
				}
			}
		})
	}
}

func TestThreads_ReducedRecordPassesTheWireCheck(t *testing.T) {
	s := &model.MariaDBStatus{
		Availability: model.MariaDBUp,
		Status:       map[string]uint64{"Uptime": math.MaxUint64, "Questions": math.MaxUint64},
		Variables:    map[string]string{"version": strings.Repeat("<", 1024)},
	}
	for i := 0; i < 4096; i++ {
		th := model.MariaDBThread{
			ID: math.MaxUint64 - uint64(i), User: strings.Repeat("<", 512), Host: strings.Repeat("<", 512), DB: strings.Repeat("&", 256),
			Command: strings.Repeat(">", 32), TimeSeconds: math.MaxUint64 - uint64(i), State: strings.Repeat("<", 256),
		}
		if i < 64 {
			th.Info, th.Truncated = strings.Repeat("<", 16384), true
		} else {
			th.Truncated = true
		}
		s.Threads = append(s.Threads, th)
	}

	fitThreads(s, 1<<20)

	rec := model.Record{Meta: model.Meta{Origin: model.OriginAgent, Source: Name, Seq: 1, CapturedAt: time.Date(2026, 10, 21, 12, 0, 0, 0, time.UTC)}, Data: s}
	size, err := wire.CheckRecord(&rec)
	if err != nil || size > 1<<20 {
		t.Errorf("CheckRecord() = %d, %v after fitThreads(1 MiB), want a line of at most 1 MiB that passes", size, err)
	}
	if len(s.Threads) == 0 || len(s.Threads) >= 4096 {
		t.Errorf("threads = %d, want some left but fewer than 4096", len(s.Threads))
	}
	if got := estimate(s); got < size {
		t.Errorf("estimate() = %d, want at least the encoded line of %d bytes", got, size)
	}
}

// textsToEncode are texts whose JSON encoding is longer than their bytes in different ways.
func textsToEncode() map[string]string {
	return map[string]string{
		"markup":              strings.Repeat("<>&", 300),
		"control characters":  strings.Repeat(string([]byte{1, 7, 0x1b, 0x1f}), 200),
		"line separators":     strings.Repeat(string(rune(0x2028))+string(rune(0x2029)), 200),
		"replacement":         strings.Repeat(string(rune(0xfffd)), 300),
		"four-byte":           strings.Repeat(string(rune(0x1f600)), 200),
		"quotes and slashes":  strings.Repeat(`"\/`, 300),
		"plain":               strings.Repeat("a", 900),
		"raw invalid bytes":   strings.Repeat(string([]byte{0xff, 0xc3}), 200),
		"mixed with newlines": strings.Repeat("a\n\t<", 200),
	}
}

// statusWithText returns a status whose variable and 20 threads hold text in every text field.
func statusWithText(text string) *model.MariaDBStatus {
	s := &model.MariaDBStatus{Availability: model.MariaDBUp, Variables: map[string]string{"version": text}}
	for i := range uint64(20) {
		s.Threads = append(s.Threads, model.MariaDBThread{
			ID: math.MaxUint64 - i, User: text, Host: text, DB: text, Command: text, TimeSeconds: math.MaxUint64, State: text, Info: text, Truncated: true,
		})
	}
	return s
}

// checkEstimate fails unless the estimate of s is at least the length of its JSON encoding.
func checkEstimate(t *testing.T, s *model.MariaDBStatus) {
	t.Helper()
	encoded, err := json.Marshal(s)
	if err != nil {
		t.Fatalf("json.Marshal() = %v", err)
	}
	if got := estimate(s); got < len(encoded) {
		t.Errorf("estimate() = %d, want at least the %d bytes the status encodes to", got, len(encoded))
	}
}

func TestEstimate_IsAnUpperBoundOfTheEncodedLine(t *testing.T) {
	for name, text := range textsToEncode() {
		t.Run(name, func(t *testing.T) { checkEstimate(t, statusWithText(text)) })
	}
	t.Run("maps of counters", func(t *testing.T) {
		s := &model.MariaDBStatus{Availability: model.MariaDBUp, Status: map[string]uint64{}, Variables: map[string]string{}}
		for _, name := range selectedStatus {
			s.Status[name] = math.MaxUint64
		}
		for _, name := range selectedVariables {
			s.Variables[name] = strings.Repeat("<", 1024)
		}

		checkEstimate(t, s)
	})
}

func TestEstimate_GrowsBySixBytesPerByteOfMarkup(t *testing.T) {
	small, large := fatThreads(3, 100, true), fatThreads(3, 1100, true)

	if grew := estimate(large) - estimate(small); grew < 2*6*1000 {
		t.Errorf("estimate() grew by %d for 2,000 more bytes of '<', want at least 12,000 (6 bytes per byte)", grew)
	}
}
