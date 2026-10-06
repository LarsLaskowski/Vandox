package store

import (
	"context"
	"errors"
	"fmt"
	"math"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

var (
	minStorable = time.Unix(0, math.MinInt64).UTC()
	maxStorable = time.Unix(0, math.MaxInt64).UTC()
)

// seqs returns the sequence numbers of records, in order.
func seqs(recs []StoredRecord) []uint64 {
	out := make([]uint64, 0, len(recs))
	for _, r := range recs {
		out = append(out, r.Record.Seq)
	}
	return out
}

func equalSeqs(a, b []uint64) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

// queryFixture writes the records TestStore_Records queries, out of capture order.
func queryFixture(t *testing.T) *Store {
	t.Helper()
	s := openStore(t, t.TempDir())
	at := func(sec int) time.Time { return baseTime.Add(time.Duration(sec) * time.Second) }
	service := model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "a", Seq: 7, CapturedAt: at(5)},
		Data: &model.ServiceState{Unit: "mariadb.service", LoadState: "loaded", ActiveState: "active"},
	}
	writeOK(t, s, agentBatch(
		metricRecord("a", "cpu", 1, at(10)),
		metricRecord("a", "cpu", 2, at(0)),
		metricRecord("a", "mem", 3, at(5)),
		metricRecord("b", "cpu", 4, at(5)),
		metricRecord("a", "cpu", 5, at(5)),
		metricRecord("a", "cpu", 6, at(20)),
		service,
		logRecord("a", "a log line", 8, at(5)),
	))
	return s
}

func TestStore_Records(t *testing.T) {
	s := queryFixture(t)
	at := func(sec int) time.Time { return baseTime.Add(time.Duration(sec) * time.Second) }
	cases := []struct {
		name string
		q    RecordQuery
		want []uint64
	}{
		{"kind only, ordered by capture time then ID", RecordQuery{Kind: model.KindMetric, From: at(0), To: at(20), Limit: 100}, []uint64{2, 3, 4, 5, 1}},
		{"source", RecordQuery{Kind: model.KindMetric, Source: "a", From: at(0), To: at(20), Limit: 100}, []uint64{2, 3, 5, 1}},
		{"source and name", RecordQuery{Kind: model.KindMetric, Source: "a", Name: "cpu", From: at(0), To: at(20), Limit: 100}, []uint64{2, 5, 1}},
		{"From is inclusive, To is exclusive", RecordQuery{Kind: model.KindMetric, From: at(5), To: at(10), Limit: 100}, []uint64{3, 4, 5}},
		{"To after the last record includes it", RecordQuery{Kind: model.KindMetric, From: at(10), To: at(21), Limit: 100}, []uint64{1, 6}},
		{"limit", RecordQuery{Kind: model.KindMetric, From: at(0), To: at(20), Limit: 2}, []uint64{2, 3}},
		{"other kind", RecordQuery{Kind: model.KindServiceState, From: at(0), To: at(20), Limit: 100}, []uint64{7}},
		{"log lines", RecordQuery{Kind: model.KindLogLine, Source: "a", From: at(0), To: at(20), Limit: 100}, []uint64{8}},
		{"unknown source", RecordQuery{Kind: model.KindMetric, Source: "zzz", From: at(0), To: at(20), Limit: 100}, []uint64{}},
		{"unknown name", RecordQuery{Kind: model.KindMetric, Source: "a", Name: "zzz", From: at(0), To: at(20), Limit: 100}, []uint64{}},
		{"empty window", RecordQuery{Kind: model.KindMetric, From: at(11), To: at(12), Limit: 100}, []uint64{}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := s.Records(context.Background(), tc.q)

			if err != nil {
				t.Fatalf("Records(%+v) error = %v, want nil", tc.q, err)
			}
			if !equalSeqs(seqs(got), tc.want) {
				t.Errorf("Records(%+v) returned seqs %v, want %v", tc.q, seqs(got), tc.want)
			}
		})
	}
}

func TestStore_Records_NamedMetricCarriesItsPayload(t *testing.T) {
	s := queryFixture(t)

	got, err := s.Records(context.Background(), RecordQuery{
		Kind: model.KindMetric, Source: "a", Name: "mem", From: baseTime, To: baseTime.Add(time.Minute), Limit: 10,
	})

	if err != nil || len(got) != 1 {
		t.Fatalf("Records() = %v, %v, want one record", got, err)
	}
	if mp, ok := got[0].Record.Data.(*model.MetricPoint); !ok || mp.Name != "mem" || mp.Value != 3 {
		t.Errorf("Records() payload = %#v, want the metric mem with value 3", got[0].Record.Data)
	}
}

// validQuery returns a query that Records accepts.
func validQuery() RecordQuery {
	return RecordQuery{Kind: model.KindMetric, From: baseTime, To: baseTime.Add(time.Hour), Limit: 10}
}

func TestStore_Records_RejectsInvalidQueries(t *testing.T) {
	cases := []struct {
		name   string
		mutate func(q *RecordQuery)
	}{
		{"unknown kind", func(q *RecordQuery) { q.Kind = "bogus" }},
		{"empty kind", func(q *RecordQuery) { q.Kind = "" }},
		{"zero From", func(q *RecordQuery) { q.From = time.Time{} }},
		{"zero To", func(q *RecordQuery) { q.To = time.Time{} }},
		{"From equals To", func(q *RecordQuery) { q.To = q.From }},
		{"From after To", func(q *RecordQuery) { q.From, q.To = q.To, q.From }},
		{"From before the storable range", func(q *RecordQuery) { q.From = minStorable.Add(-time.Nanosecond) }},
		{"To after the storable range", func(q *RecordQuery) { q.To = maxStorable.Add(time.Nanosecond) }},
		{"limit zero", func(q *RecordQuery) { q.Limit = 0 }},
		{"limit negative", func(q *RecordQuery) { q.Limit = -1 }},
		{"limit above MaxQueryLimit", func(q *RecordQuery) { q.Limit = MaxQueryLimit + 1 }},
		{"name with another kind", func(q *RecordQuery) { q.Kind, q.Source, q.Name = model.KindLogLine, "node", "cpu" }},
		{"name without source", func(q *RecordQuery) { q.Name = "cpu" }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			q := validQuery()
			tc.mutate(&q)

			got, err := s.Records(context.Background(), q)

			if !errors.Is(err, ErrInvalidQuery) {
				t.Errorf("Records(%+v) = %v, %v, want an error wrapping ErrInvalidQuery", q, got, err)
			}
		})
	}

	t.Run("boundary values are accepted", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		q := RecordQuery{Kind: model.KindMetric, From: minStorable, To: maxStorable, Limit: MaxQueryLimit}

		if _, err := s.Records(context.Background(), q); err != nil {
			t.Errorf("Records(%+v) error = %v, want nil", q, err)
		}
	})
}

// queryPlan returns the EXPLAIN QUERY PLAN details of the SQL recordsQuery builds for q.
func queryPlan(t *testing.T, s *Store, q RecordQuery) string {
	t.Helper()
	query, args := recordsQuery(q)
	if query == "" {
		t.Fatal("recordsQuery() = empty query, want SQL")
	}
	rows, err := s.db.QueryContext(context.Background(), "EXPLAIN QUERY PLAN "+query, args...)
	if err != nil {
		t.Fatalf("EXPLAIN QUERY PLAN error = %v, want nil", err)
	}
	defer func() { _ = rows.Close() }()
	var plan []string
	for rows.Next() {
		var id, parent, unused int
		var detail string
		if err := rows.Scan(&id, &parent, &unused, &detail); err != nil {
			t.Fatalf("scanning the plan error = %v, want nil", err)
		}
		plan = append(plan, detail)
	}
	if err := rows.Err(); err != nil {
		t.Fatalf("reading the plan error = %v, want nil", err)
	}
	return strings.Join(plan, "\n")
}

func TestRecordsQuery_UsesIndexesWithoutSorting(t *testing.T) {
	window := func(q RecordQuery) RecordQuery {
		q.From, q.To, q.Limit = baseTime, baseTime.Add(time.Hour), 10
		return q
	}
	cases := []struct {
		name      string
		q         RecordQuery
		wantIndex string
	}{
		{"metric with source and name", window(RecordQuery{Kind: model.KindMetric, Source: "node", Name: "cpu.load"}), "metrics_source_name_time"},
		{"kind and source", window(RecordQuery{Kind: model.KindServiceState, Source: "node"}), "records_kind_source_time"},
		{"kind only", window(RecordQuery{Kind: model.KindServiceState}), "records_kind_time"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())

			plan := queryPlan(t, s, tc.q)

			if !strings.Contains(plan, tc.wantIndex) {
				t.Errorf("query plan = %q, want it to use %s", plan, tc.wantIndex)
			}
			if strings.Contains(plan, "USE TEMP B-TREE") {
				t.Errorf("query plan = %q, want no USE TEMP B-TREE", plan)
			}
		})
	}
}

func TestStore_Records_ContextAndClosedStore(t *testing.T) {
	t.Run("cancelled context", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		ctx, cancel := context.WithCancel(context.Background())
		cancel()

		if _, err := s.Records(ctx, validQuery()); err == nil {
			t.Error("Records(cancelled ctx) error = nil, want an error")
		}
	})

	t.Run("closed store", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		if err := s.Close(); err != nil {
			t.Fatalf("Close() error = %v, want nil", err)
		}

		if _, err := s.Records(context.Background(), validQuery()); err == nil {
			t.Error("Records() on a closed store error = nil, want an error")
		}
	})
}

// searchMessages returns the log messages of recs, in order.
func searchMessages(recs []StoredRecord) []string {
	out := make([]string, 0, len(recs))
	for _, r := range recs {
		out = append(out, r.Record.Data.(*model.LogLine).Message)
	}
	return out
}

func search(t *testing.T, s *Store, q LogSearch) []StoredRecord {
	t.Helper()
	got, err := s.SearchLogs(context.Background(), q)
	if err != nil {
		t.Fatalf("SearchLogs(%+v) error = %v, want nil", q, err)
	}
	return got
}

// validSearch returns a search that SearchLogs accepts.
func validSearch(text string) LogSearch {
	return LogSearch{Text: text, From: baseTime.Add(-time.Hour), To: baseTime.Add(time.Hour), Limit: 100}
}

func TestStore_SearchLogs(t *testing.T) {
	s := openStore(t, t.TempDir())
	at := func(sec int) time.Time { return baseTime.Add(time.Duration(sec) * time.Second) }
	const (
		oom    = "Out of memory: Killed process 123 (mariadbd) anon-rss:1kB"
		german = "Fehlerübersicht gefunden"
		outOnl = "only out here"
		memOnl = "memory only here"
		both   = "out memory both"
	)
	writeOK(t, s, agentBatch(
		logRecord("a", oom, 1, at(1)),
		logRecord("b", german, 2, at(2)),
		logRecord("a", outOnl, 3, at(3)),
		logRecord("a", memOnl, 4, at(4)),
		logRecord("b", both, 5, at(5)),
		metricRecord("a", "memory", 6, at(6)),
	))
	cases := []struct {
		name string
		q    LogSearch
		want []string
	}{
		{"one word", LogSearch{Text: "mariadbd"}, []string{oom}},
		{"two words that both occur", LogSearch{Text: "out memory"}, []string{oom, both}},
		{"case-insensitive", LogSearch{Text: "MARIADBD"}, []string{oom}},
		{"diacritics are ignored", LogSearch{Text: "fehlerubersicht"}, []string{german}},
		{"diacritics in the query are ignored", LogSearch{Text: "Fehlerübersicht"}, []string{german}},
		{"term with punctuation as a phrase", LogSearch{Text: "anon-rss"}, []string{oom}},
		{"source filter", LogSearch{Text: "memory", Source: "b"}, []string{both}},
		{"no source filter", LogSearch{Text: "memory"}, []string{oom, memOnl, both}},
		{"time range is half-open", LogSearch{Text: "memory", From: at(4), To: at(5)}, []string{memOnl}},
		{"limit", LogSearch{Text: "memory", Limit: 2}, []string{oom, memOnl}},
		{"no hit", LogSearch{Text: "absent"}, []string{}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			q := tc.q
			if q.From.IsZero() {
				q.From = at(0)
			}
			if q.To.IsZero() {
				q.To = at(60)
			}
			if q.Limit == 0 {
				q.Limit = 100
			}

			got := searchMessages(search(t, s, q))

			if strings.Join(got, "|") != strings.Join(tc.want, "|") {
				t.Errorf("SearchLogs(%+v) = %q, want %q", q, got, tc.want)
			}
		})
	}
}

// operatorFixture stores one log line per FTS5 operator form and one with characters that are refused.
func operatorFixture(t *testing.T) *Store {
	t.Helper()
	s := openStore(t, t.TempDir())
	lines := []string{
		"this OR that happened",
		"NOT found here",
		"AND then some",
		"near a b words",
		"process killed",
		"kill it",
		"start of line",
		"message x here",
		"see a b here",
		"item (a) value",
		"a quoted word",
		"price \u00bd and \ue000 private",
	}
	recs := make([]model.Record, 0, len(lines))
	for i, l := range lines {
		recs = append(recs, logRecord("node", l, uint64(i+1), baseTime.Add(time.Duration(i)*time.Second)))
	}
	writeOK(t, s, agentBatch(recs...))
	return s
}

func TestStore_SearchLogs_OperatorsAreLiteral(t *testing.T) {
	s := operatorFixture(t)
	cases := []struct {
		text string
		want string
	}{
		{"OR", "this OR that happened"},
		{"NOT", "NOT found here"},
		{"AND", "AND then some"},
		{"NEAR(a b)", "near a b words"},
		{"kill*", "kill it"},
		{"^start", "start of line"},
		{"message:x", "message x here"},
		{"{message}:x", "message x here"},
		{"-message:x", "message x here"},
		{"a+b", "see a b here"},
		{"(a)", "item (a) value"},
		{`"quoted"`, "a quoted word"},
		{`a"b`, "see a b here"},
	}
	for _, tc := range cases {
		t.Run(tc.text, func(t *testing.T) {
			got := searchMessages(search(t, s, validSearch(tc.text)))

			if !slices.Contains(got, tc.want) {
				t.Errorf("SearchLogs(%q) = %q, want it to contain %q", tc.text, got, tc.want)
			}
		})
	}
}

func TestStore_SearchLogs_PrefixStarDoesNotMatchLongerWords(t *testing.T) {
	s := operatorFixture(t)

	got := searchMessages(search(t, s, validSearch("kill*")))

	if slices.Contains(got, "process killed") {
		t.Errorf("SearchLogs(%q) = %q, want it not to contain %q", "kill*", got, "process killed")
	}
}

func TestStore_SearchLogs_RefusesTermsOfOnlyNumbersOrPrivateUse(t *testing.T) {
	s := operatorFixture(t)
	for _, text := range []string{"\u00bd", "\ue000"} {
		t.Run(fmt.Sprintf("%U", []rune(text)[0]), func(t *testing.T) {
			_, err := s.SearchLogs(context.Background(), validSearch(text))

			if !errors.Is(err, ErrInvalidQuery) {
				t.Errorf("SearchLogs(%q) error = %v, want an error wrapping ErrInvalidQuery", text, err)
			}
		})
	}
}

func TestFtsQuery(t *testing.T) {
	terms16 := strings.TrimSpace(strings.Repeat("t ", 16))
	cases := []struct {
		name string
		text string
		want string // empty: an error is expected
	}{
		{"bareword", "oom", `"oom"`},
		{"several barewords", "out memory", `"out" "memory"`},
		{"quote is doubled", `a"b`, `"a""b"`},
		{"quoted word", `"quoted"`, `"""quoted"""`},
		{"prefix star", "kill*", `"kill*"`},
		{"initial token caret", "^start", `"^start"`},
		{"concatenation plus", "a+b", `"a+b"`},
		{"plus as its own term", "a + b", ""},
		{"NEAR", "NEAR(a b)", `"NEAR(a" "b)"`},
		{"NEAR with distance", "NEAR(a b, 5)", `"NEAR(a" "b," "5)"`},
		{"OR", "OR", `"OR"`},
		{"AND", "AND", `"AND"`},
		{"NOT", "NOT", `"NOT"`},
		{"parentheses", "(a)", `"(a)"`},
		{"column filter", "message:x", `"message:x"`},
		{"column filter braces", "{message}:x", `"{message}:x"`},
		{"negated column filter", "-message:x", `"-message:x"`},
		{"dashes only", "--", ""},
		{"star only", "*", ""},
		{"quote only", `"`, ""},
		{"vulgar fraction only", "\u00bd", ""},
		{"superscript only", "\u00b2", ""},
		{"roman numeral only", "\u216b", ""},
		{"private use only", "\ue000", ""},
		{"vulgar fraction next to a letter", "\u00bdx", "\"\u00bdx\""},
		{"superscript next to a letter", "x\u00b2", "\"x\u00b2\""},
		{"empty", "", ""},
		{"white space only", "  ", ""},
		{"NUL", "a\x00b", ""},
		{"escape control", "a\x1bb", ""},
		{"byte order mark", "\ufeffoom", ""},
		{"zero width space", "\u200boom", ""},
		{"bidi control", "\u202eoom", ""},
		{"tab separates", "a\tb", `"a" "b"`},
		{"newline separates", "a\nb", `"a" "b"`},
		{"carriage return separates", "a\rb", `"a" "b"`},
		{"vertical tab separates", "a\vb", `"a" "b"`},
		{"form feed separates", "a\fb", `"a" "b"`},
		{"next line separates", "a\u0085b", `"a" "b"`},
		{"no-break space separates", "a\u00a0b", `"a" "b"`},
		{"line separator separates", "a\u2028b", `"a" "b"`},
		{"ideographic space separates", "a\u3000b", `"a" "b"`},
		{"repeated spaces", "  a   b  ", `"a" "b"`},
		{"diacritics", "Größe", `"Größe"`},
		{"non-Latin script", "ошибка", `"ошибка"`},
		{"invalid UTF-8", "\xff", ""},
		{"invalid UTF-8 inside a term", "a\xffb", ""},
		{"1024 bytes", strings.Repeat("a", 1024), `"` + strings.Repeat("a", 1024) + `"`},
		{"1025 bytes", strings.Repeat("a", 1025), ""},
		{"16 terms", terms16, `"` + strings.ReplaceAll(terms16, " ", `" "`) + `"`},
		{"17 terms", terms16 + " t", ""},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := ftsQuery(tc.text)

			if tc.want == "" {
				if !errors.Is(err, ErrInvalidQuery) {
					t.Errorf("ftsQuery(%q) = %q, %v, want an error wrapping ErrInvalidQuery", tc.text, got, err)
				}
				return
			}
			if err != nil || got != tc.want {
				t.Errorf("ftsQuery(%q) = %q, %v, want %q, nil", tc.text, got, err, tc.want)
			}
		})
	}
}

func TestStore_SearchLogs_RejectsInvalidQueries(t *testing.T) {
	const secret = "SEARCHSECRET"
	cases := []struct {
		name   string
		mutate func(q *LogSearch)
	}{
		{"zero From", func(q *LogSearch) { q.From = time.Time{} }},
		{"zero To", func(q *LogSearch) { q.To = time.Time{} }},
		{"From equals To", func(q *LogSearch) { q.To = q.From }},
		{"From after To", func(q *LogSearch) { q.From, q.To = q.To, q.From }},
		{"From before the storable range", func(q *LogSearch) { q.From = minStorable.Add(-time.Nanosecond) }},
		{"To after the storable range", func(q *LogSearch) { q.To = maxStorable.Add(time.Nanosecond) }},
		{"limit zero", func(q *LogSearch) { q.Limit = 0 }},
		{"limit above MaxQueryLimit", func(q *LogSearch) { q.Limit = MaxQueryLimit + 1 }},
		{"empty text", func(q *LogSearch) { q.Text = "" }},
		{"NUL in the text", func(q *LogSearch) { q.Text = secret + "\x00" }},
		{"too many terms", func(q *LogSearch) { q.Text = secret + strings.Repeat(" t", MaxSearchTerms) }},
		{"text too long", func(q *LogSearch) { q.Text = secret + strings.Repeat("x", MaxSearchBytes) }},
		{"invalid UTF-8", func(q *LogSearch) { q.Text = secret + "\xff" }},
		{"punctuation-only term", func(q *LogSearch) { q.Text = secret + " --" }},
		{"format character", func(q *LogSearch) { q.Text = secret + " \u200b" }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			q := validSearch(secret)
			tc.mutate(&q)

			got, err := s.SearchLogs(context.Background(), q)

			if !errors.Is(err, ErrInvalidQuery) {
				t.Fatalf("SearchLogs() = %v, %v, want an error wrapping ErrInvalidQuery", got, err)
			}
			if strings.Contains(err.Error(), secret) {
				t.Errorf("SearchLogs() error = %q, want it not to contain the search text", err)
			}
		})
	}

	t.Run("ftsQuery errors do not contain the text", func(t *testing.T) {
		for _, text := range []string{secret + "\x00", secret + " --", secret + "\xff", secret + strings.Repeat("x", MaxSearchBytes)} {
			_, err := ftsQuery(text)

			if err == nil || strings.Contains(err.Error(), secret) {
				t.Errorf("ftsQuery() error = %v, want an error without the search text", err)
			}
		}
	})
}

func TestStore_SearchLogs_ContextAndClosedStore(t *testing.T) {
	t.Run("cancelled context", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		ctx, cancel := context.WithCancel(context.Background())
		cancel()

		_, err := s.SearchLogs(ctx, validSearch("oom"))

		if !errors.Is(err, context.Canceled) {
			t.Errorf("SearchLogs(cancelled ctx) error = %v, want it to wrap context.Canceled", err)
		}
	})

	t.Run("closed store", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		if err := s.Close(); err != nil {
			t.Fatalf("Close() error = %v, want nil", err)
		}

		if _, err := s.SearchLogs(context.Background(), validSearch("oom")); err == nil {
			t.Error("SearchLogs() on a closed store error = nil, want an error")
		}
	})
}
