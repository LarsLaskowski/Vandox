package main

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/importer"
	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/internal/logparse"
	"github.com/LarsLaskowski/Vandox/internal/logparse/logparsetest"
)

var (
	importFirst = time.Date(2026, time.October, 1, 10, 0, 0, 0, time.UTC)
	importLast  = time.Date(2026, time.October, 5, 12, 30, 45, 0, time.UTC)
	importNow   = time.Date(2026, time.October, 7, 8, 0, 0, 0, time.UTC)
)

// alphaParsers returns one parser of type "syslog" that claims the files whose content starts with "alpha".
func alphaParsers() []logparse.Parser {
	return []logparse.Parser{&logparsetest.Parser{
		TypeName:   "syslog",
		DetectFunc: logparsetest.HeadPrefix("alpha", logparse.MatchContent),
		ParseFunc:  logparsetest.Lines("syslog", importFirst),
	}}
}

// importRun is the result of one importCommand call.
type importRun struct {
	code           int
	stdout, stderr string
}

// runImport runs importCommand with the configuration cfg (written to a file), the parsers and the arguments after
// "import"; "{config}" in args is replaced by the configuration file.
func runImport(ctx context.Context, t *testing.T, cfg serviceConfig, parsers []logparse.Parser, args ...string) importRun {
	t.Helper()
	path := cfg.write(t)
	for i := range args {
		args[i] = strings.ReplaceAll(args[i], "{config}", path)
	}
	var stdout, stderr syncBuffer
	code := importCommand(ctx, args, path, nil, &stdout, &stderr, importEnv{parsers: parsers, now: func() time.Time { return importNow }})
	return importRun{code, stdout.String(), stderr.String()}
}

// importDir returns a directory with the given files.
func importDir(t *testing.T, files map[string][]byte) string {
	t.Helper()
	root := filepath.Join(t.TempDir(), "in")
	if err := os.Mkdir(root, 0o700); err != nil {
		t.Fatal(err)
	}
	for name, data := range files {
		if err := os.WriteFile(filepath.Join(root, name), data, 0o600); err != nil {
			t.Fatal(err)
		}
	}
	return root
}

// gzipOf returns the gzip compression of s.
func gzipOf(t *testing.T, s string) []byte {
	t.Helper()
	var buf bytes.Buffer
	w := gzip.NewWriter(&buf)
	if _, err := w.Write([]byte(s)); err != nil {
		t.Fatal(err)
	}
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

// storedLines returns the number of log lines in the database of cfg.
func storedLines(t *testing.T, cfg serviceConfig) int {
	t.Helper()
	s, err := store.Open(context.Background(), cfg.dir)
	if err != nil {
		t.Fatalf("store.Open() error = %v, want nil", err)
	}
	defer func() { _ = s.Close() }()
	recs, err := s.Records(context.Background(), store.RecordQuery{
		Kind: "log_line", From: time.Date(2020, 1, 1, 0, 0, 0, 0, time.UTC), To: time.Date(2100, 1, 1, 0, 0, 0, 0, time.UTC), Limit: store.MaxQueryLimit,
	})
	if err != nil {
		t.Fatalf("Records() error = %v, want nil", err)
	}
	return len(recs)
}

// jsonLines decodes every line of s as a JSON object and fails the test when a line is not one.
func jsonLines(t *testing.T, s string) []map[string]any {
	t.Helper()
	var out []map[string]any
	for _, line := range strings.Split(strings.TrimSuffix(s, "\n"), "\n") {
		if line == "" {
			continue
		}
		var m map[string]any
		if err := json.Unmarshal([]byte(line), &m); err != nil {
			t.Fatalf("stderr line %q is not a JSON object: %v", line, err)
		}
		out = append(out, m)
	}
	return out
}

// ---- AC-C6 ----

func TestImportParsers_NoneUntilTheParsersExist(t *testing.T) {
	if got := importParsers(); len(got) != 0 {
		t.Errorf("importParsers() = %d parsers, want none until #16 adds the first", len(got))
	}
}

// ---- AC-C1: dispatch, flags, summary ----

func TestRun_ImportDispatchAndConfigFlag(t *testing.T) {
	tests := []struct {
		name string
		args func(cfgPath, root string) []string
	}{
		{"global -config before import", func(cfg, root string) []string { return []string{"-config", cfg, "import", root} }},
		{"-config after import", func(cfg, root string) []string { return []string{"import", "-config", cfg, root} }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			cfg := newServiceConfig(t)
			root := importDir(t, map[string][]byte{"a.log": []byte("alpha 1\n"), "b.log": []byte("beta 1\n")})
			args := tc.args(cfg.write(t), root)
			var stdout, stderr syncBuffer

			code := run(t.Context(), args, nil, &stdout, &stderr, noListen(t))

			if code != 0 {
				t.Fatalf("run(%v) exit code = %d, want 0; stderr: %s", args, code, stderr.String())
			}
			for _, want := range []string{strconv.Quote("a.log"), strconv.Quote("b.log"), strconv.Quote("no parser recognized the file")} {
				if !strings.Contains(stdout.String(), want) {
					t.Errorf("run(%v) stdout = %q, want it to list %s (no parser exists yet, every file is not recognized)", args, stdout.String(), want)
				}
			}
			if _, err := os.Stat(filepath.Join(cfg.dir, store.FileName)); err != nil {
				t.Errorf("database in the configured storage directory: %v, want it opened there", err)
			}
		})
	}
}

func TestRun_ImportInnerConfigFlagOverridesTheGlobalOne(t *testing.T) {
	cfg := newServiceConfig(t)
	root := importDir(t, map[string][]byte{"a.log": []byte("alpha 1\n")})
	var stdout, stderr syncBuffer

	code := run(t.Context(), []string{"-config", filepath.Join(t.TempDir(), "absent.yaml"), "import", "-config", cfg.write(t), root}, nil, &stdout, &stderr, noListen(t))

	if code != 0 {
		t.Errorf("run() exit code = %d, want 0 (the flag after import wins); stderr: %s", code, stderr.String())
	}
}

func TestImportCommand_StoresRecordsAndIsRepeatable(t *testing.T) {
	cfg := newServiceConfig(t)
	root := importDir(t, map[string][]byte{"a.log": []byte("alpha 1\nalpha 2\nalpha 3\n"), "z.gz": gzipOf(t, "alpha 9\n")})

	first := runImport(t.Context(), t, cfg, alphaParsers(), root)
	second := runImport(t.Context(), t, cfg, alphaParsers(), root)

	if first.code != 0 || second.code != 0 {
		t.Fatalf("exit codes = %d, %d, want 0, 0; stderr: %s %s", first.code, second.code, first.stderr, second.stderr)
	}
	if got := storedLines(t, cfg); got != 4 {
		t.Errorf("stored log lines after two imports = %d, want 4 (the second import stores nothing)", got)
	}
	if first.stdout == second.stdout {
		t.Errorf("both summaries are %q, want the second to report the files as already imported", first.stdout)
	}
}

// ---- AC-C1, AC-C2: the summary text ----

// numberToken matches n as a whole number in the output.
func numberToken(n int) *regexp.Regexp { return regexp.MustCompile(`\b` + strconv.Itoa(n) + `\b`) }

// craftedSummary returns a summary with 6 imported, 9 already imported, 4 unrecognized and 8 failed files.
func craftedSummary() importer.Summary {
	s := importer.Summary{Root: "/import/x", Started: importNow, Finished: importNow.Add(time.Minute), Lines: 321, Records: 468, Skipped: 99, First: importFirst, Last: importLast}
	letters := "abcdefghij"
	for i := range 6 {
		s.Files = append(s.Files, importer.FileResult{Path: "imp-" + string(letters[i]) + ".log", Outcome: importer.OutcomeImported, SourceType: "syslog"})
	}
	s.Files[0].Skipped = 31
	s.Files[0].Path = "skipper.log"
	s.Files[0].Problems = []importer.Problem{{Line: 12, Reason: "first problem"}, {Line: 15, Reason: "second problem"}}
	for i := range 9 {
		s.Files = append(s.Files, importer.FileResult{Path: "old-" + string(letters[i]) + ".log", Outcome: importer.OutcomeAlreadyImported, SourceType: "syslog"})
	}
	for i := range 4 {
		s.Files = append(s.Files, importer.FileResult{Path: "unrec-" + string(letters[i]) + ".bin", Outcome: importer.OutcomeUnrecognized, Reason: "reason-unrec-" + string(letters[i])})
	}
	for i := range 8 {
		s.Files = append(s.Files, importer.FileResult{Path: "fail-" + string(letters[i]) + ".gz", Outcome: importer.OutcomeFailed, Reason: "reason-fail-" + string(letters[i])})
	}
	return s
}

// assertContainsAll fails the test for every want that text does not contain.
func assertContainsAll(t *testing.T, text, what string, wants ...string) {
	t.Helper()
	for _, want := range wants {
		if !strings.Contains(text, want) {
			t.Errorf("summary = %q, want %s %s", text, what, want)
		}
	}
}

// assertListed checks that the summary lists the path and the reason of every file with an outcome in outcomes.
func assertListed(t *testing.T, text string, files []importer.FileResult, outcomes ...importer.Outcome) {
	t.Helper()
	for _, f := range files {
		if slices.Contains(outcomes, f.Outcome) {
			assertContainsAll(t, text, "the "+string(f.Outcome)+" file listed with", strconv.Quote(f.Path), strconv.Quote(f.Reason))
		}
	}
}

func TestWriteSummary_StatesCountsTotalsRangeAndLists(t *testing.T) {
	s := craftedSummary()
	var out bytes.Buffer

	if err := writeSummary(&out, s); err != nil {
		t.Fatalf("writeSummary() error = %v, want nil", err)
	}

	text := out.String()
	for _, n := range []int{6, 9, 4, 8, 321, 468, 99} {
		if !numberToken(n).MatchString(text) {
			t.Errorf("summary = %q, want the number %d (counts per outcome, lines, records, skipped)", text, n)
		}
	}
	assertContainsAll(t, text, "the time range bound", importFirst.Format(time.RFC3339), importLast.Format(time.RFC3339))
	assertListed(t, text, s.Files, importer.OutcomeUnrecognized, importer.OutcomeFailed)
	assertContainsAll(t, text, "the file with skipped lines and its problems", strconv.Quote("skipper.log"), strconv.Quote("first problem"), strconv.Quote("second problem"))
}

func TestWriteSummary_WithoutRecordsSaysSo(t *testing.T) {
	s := importer.Summary{Root: "/import/x", Started: importNow, Finished: importNow, Files: []importer.FileResult{
		{Path: "a.bin", Outcome: importer.OutcomeUnrecognized, Reason: "empty"},
	}}
	var out bytes.Buffer

	if err := writeSummary(&out, s); err != nil {
		t.Fatalf("writeSummary() error = %v, want nil", err)
	}

	text := out.String()
	if !strings.Contains(strings.ToLower(text), "no records") {
		t.Errorf("summary = %q, want it to say that no records were stored", text)
	}
	if strings.Contains(text, "0001-01-01") {
		t.Errorf("summary = %q, want no zero time printed as a time range", text)
	}
}

func TestWriteSummary_ShowsAnInterruptedRun(t *testing.T) {
	s := craftedSummary()
	s.Interrupted = true
	var out bytes.Buffer

	if err := writeSummary(&out, s); err != nil {
		t.Fatalf("writeSummary() error = %v, want nil", err)
	}

	if !strings.Contains(strings.ToLower(out.String()), "interrupted") {
		t.Errorf("summary = %q, want it to say the import was interrupted", out.String())
	}
}

func TestWriteSummary_ReturnsWriteErrors(t *testing.T) {
	if err := writeSummary(failingWriter{}, craftedSummary()); err == nil {
		t.Error("writeSummary() to a failing writer error = nil, want the write error")
	}
}

// hostileNames are names that must not forge lines or send escape sequences to a terminal.
func hostileNames() map[string]string {
	return map[string]string{
		"newline and ANSI escape": "evil\n\x1b[31mred-forged-line",
		"C1 CSI and RTL override": "csi\u009b2Jrlo\u202egnp.log",
		"DEL and NUL-free mix":    "del\x7fend\u2028sep",
	}
}

func TestWriteSummary_QuotesEveryPathAndReason(t *testing.T) {
	for label, name := range hostileNames() {
		t.Run(label, func(t *testing.T) {
			s := importer.Summary{Root: "/import/x", Files: []importer.FileResult{
				{Path: name, Outcome: importer.OutcomeUnrecognized, Reason: "reason " + name},
				{Path: name + "-f", Outcome: importer.OutcomeFailed, Reason: "failed " + name},
				{Path: name + "-s", Outcome: importer.OutcomeImported, Skipped: 1, Problems: []importer.Problem{{Line: 1, Reason: "problem " + name}}},
			}}
			var out bytes.Buffer

			if err := writeSummary(&out, s); err != nil {
				t.Fatalf("writeSummary() error = %v, want nil", err)
			}

			text := out.String()
			assertNoRawControls(t, text)
			for _, want := range []string{strconv.Quote(name), strconv.Quote("reason " + name), strconv.Quote("failed " + name), strconv.Quote(name + "-s")} {
				if !strings.Contains(text, want) {
					t.Errorf("summary = %q, want %s quoted", text, want)
				}
			}
			if forged := lineStarting(text, "red-forged-line"); forged {
				t.Errorf("summary = %q, want no line forged by a newline in a name", text)
			}
		})
	}
}

// lineStarting reports whether a line of text starts with prefix.
func lineStarting(text, prefix string) bool {
	for _, line := range strings.Split(text, "\n") {
		if strings.HasPrefix(line, prefix) {
			return true
		}
	}
	return false
}

// assertNoRawControls fails the test when text holds a control, C1 or format character that a terminal acts on.
func assertNoRawControls(t *testing.T, text string) {
	t.Helper()
	for _, raw := range []string{"\x1b", "\x7f", "\u009b", "\u202e", "\u2028", "\xc2\x9b", "\xe2\x80\xae"} {
		if strings.Contains(text, raw) {
			t.Errorf("output %q holds the raw character %q, want it escaped", text, raw)
		}
	}
}

func TestWriteSummary_EscapesAsGoQuotes(t *testing.T) {
	s := importer.Summary{Files: []importer.FileResult{{Path: "a\u009bb\u202ec", Outcome: importer.OutcomeUnrecognized, Reason: "r"}}}
	var out bytes.Buffer

	if err := writeSummary(&out, s); err != nil {
		t.Fatalf("writeSummary() error = %v, want nil", err)
	}

	if text := out.String(); !strings.Contains(text, `\u009b`) || !strings.Contains(text, `\u202e`) {
		t.Errorf("summary = %q, want the escapes %s and %s", text, `\u009b`, `\u202e`)
	}
}

func TestImportCommand_SummaryOfHostileFileNamesIsOneEscapedLinePerFile(t *testing.T) {
	cfg := newServiceConfig(t)
	files := map[string][]byte{}
	for _, name := range hostileNames() {
		files[strings.ReplaceAll(name, "/", "_")] = []byte("beta never claimed\n")
	}
	root := importDir(t, files)

	got := runImport(t.Context(), t, cfg, alphaParsers(), root)

	if got.code != 0 {
		t.Fatalf("exit code = %d, want 0; stderr: %s", got.code, got.stderr)
	}
	assertNoRawControls(t, got.stdout)
	for _, name := range hostileNames() {
		if want := strconv.Quote(strings.ReplaceAll(name, "/", "_")); !strings.Contains(got.stdout, want) {
			t.Errorf("stdout = %q, want %s", got.stdout, want)
		}
	}
	if lineStarting(got.stdout, "red-forged-line") {
		t.Errorf("stdout = %q, want no line forged by a newline in a file name", got.stdout)
	}
}

// ---- AC-C3: exit codes ----

func TestImportCommand_ExitCodeOneWhenAFileFailed(t *testing.T) {
	cfg := newServiceConfig(t)
	full := gzipOf(t, strings.Repeat("alpha line\n", 500))
	root := importDir(t, map[string][]byte{"a-good.log": []byte("alpha 1\n"), "b-bad.gz": full[:len(full)/2], "c-unclaimed.log": []byte("beta\n")})

	got := runImport(t.Context(), t, cfg, alphaParsers(), root)

	if got.code != 1 {
		t.Errorf("exit code = %d, want 1 when a file failed", got.code)
	}
	for _, want := range []string{strconv.Quote("b-bad.gz"), strconv.Quote("c-unclaimed.log")} {
		if !strings.Contains(got.stdout, want) {
			t.Errorf("stdout = %q, want the summary to list %s", got.stdout, want)
		}
	}
	if storedLines(t, cfg) != 1 {
		t.Errorf("stored lines = %d, want 1 (the good file is imported, the run continues)", storedLines(t, cfg))
	}
}

func TestImportCommand_ExitCodeZeroWhenOnlyUnrecognizedFilesAreListed(t *testing.T) {
	cfg := newServiceConfig(t)
	root := importDir(t, map[string][]byte{"a.log": []byte("beta\n"), "b.log": nil})

	got := runImport(t.Context(), t, cfg, alphaParsers(), root)

	if got.code != 0 {
		t.Errorf("exit code = %d, want 0 when every file was imported, already imported or not recognized; stderr: %s", got.code, got.stderr)
	}
}

func TestImportCommand_InterruptedRunExitsOneWithTheSummary(t *testing.T) {
	cfg := newServiceConfig(t)
	root := importDir(t, map[string][]byte{"a.log": []byte(strings.Repeat("alpha line\n", 50000))})
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	parsers := []logparse.Parser{&logparsetest.Parser{
		TypeName:   "syslog",
		DetectFunc: logparsetest.HeadPrefix("alpha", logparse.MatchContent),
		ParseFunc: func(_ context.Context, _ logparse.File, r io.Reader, _ logparse.Emitter) error {
			buf := make([]byte, 64)
			for {
				if _, err := r.Read(buf); err != nil {
					return err
				}
				cancel()
			}
		},
	}}

	got := runImport(ctx, t, cfg, parsers, root)

	if got.code != 1 {
		t.Errorf("exit code = %d, want 1 for an interrupted import", got.code)
	}
	if !strings.Contains(strings.ToLower(got.stdout), "interrupted") {
		t.Errorf("stdout = %q, want the summary printed and saying the import was interrupted", got.stdout)
	}
}

func TestImportCommand_ErrorsBeforeTheImportExitOneWithAJSONErrorLine(t *testing.T) {
	root := importDir(t, map[string][]byte{"a.log": []byte("alpha\n")})
	invalid := newServiceConfig(t)
	invalid.level = "loud"
	blocked := newServiceConfig(t)
	if err := os.Mkdir(filepath.Join(blocked.dir, store.FileName), 0o700); err != nil {
		t.Fatal(err)
	}
	tests := []struct {
		name string
		cfg  serviceConfig
		root string
	}{
		{"invalid configuration", invalid, root},
		{"database that cannot be opened", blocked, root},
		{"missing root", newServiceConfig(t), filepath.Join(t.TempDir(), "missing")},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := runImport(t.Context(), t, tc.cfg, alphaParsers(), tc.root)

			if got.code != 1 {
				t.Errorf("exit code = %d, want 1", got.code)
			}
			lines := jsonLines(t, got.stderr)
			if len(lines) == 0 {
				t.Fatalf("stderr = %q, want a JSON error line", got.stderr)
			}
			last := lines[len(lines)-1]
			if last["level"] != "ERROR" || last["error"] == nil {
				t.Errorf("last stderr line = %v, want level ERROR with an error attribute", last)
			}
		})
	}
	t.Run("configuration file that does not exist", func(t *testing.T) {
		var stdout, stderr syncBuffer

		code := importCommand(t.Context(), []string{root}, filepath.Join(t.TempDir(), "absent.yaml"), nil, &stdout, &stderr, importEnv{parsers: alphaParsers(), now: time.Now})

		if code != 1 || len(jsonLines(t, stderr.String())) == 0 {
			t.Errorf("exit code = %d, stderr = %q, want 1 and a JSON error line", code, stderr.String())
		}
	})
}

// ---- AC-C5: progress logging ----

func TestImportCommand_LogsOneFileFinishedLinePerFile(t *testing.T) {
	cfg := newServiceConfig(t)
	root := importDir(t, map[string][]byte{"a.log": []byte("alpha 1\n"), "b.log": []byte("beta\n"), "c.gz": gzipOf(t, "alpha 2\n")})

	got := runImport(t.Context(), t, cfg, alphaParsers(), root)

	if got.code != 0 {
		t.Fatalf("exit code = %d, want 0; stderr: %s", got.code, got.stderr)
	}
	finished := map[string]map[string]any{}
	for _, m := range jsonLines(t, got.stderr) {
		msg, _ := m["msg"].(string)
		if strings.Contains(msg, "a.log") || strings.Contains(msg, "b.log") || strings.Contains(msg, "c.gz") {
			t.Errorf("message %q contains a file name, want names only as attributes", msg)
		}
		if msg != "file finished" {
			continue
		}
		if m["level"] != "INFO" {
			t.Errorf("file finished line level = %v, want INFO", m["level"])
		}
		path, _ := m["path"].(string)
		finished[path] = m
	}
	if len(finished) != 3 {
		t.Errorf("file finished lines = %d, want 3 (one per file)", len(finished))
	}
	for name, outcome := range map[string]string{"a.log": "imported", "b.log": "unrecognized", "c.gz": "imported"} {
		m, ok := finished[strconv.Quote(name)]
		if !ok {
			t.Errorf("no file finished line for %s, want one with path %s; lines: %v", name, strconv.Quote(name), finished)
			continue
		}
		if m["outcome"] != outcome {
			t.Errorf("file finished line of %s outcome = %v, want %q", name, m["outcome"], outcome)
		}
	}
}

func TestImportCommand_LogLevelOfTheConfigurationFiltersProgress(t *testing.T) {
	cfg := newServiceConfig(t)
	cfg.level = "warn"
	root := importDir(t, map[string][]byte{"a.log": []byte("alpha 1\n")})

	got := runImport(t.Context(), t, cfg, alphaParsers(), root)

	if got.code != 0 {
		t.Fatalf("exit code = %d, want 0; stderr: %s", got.code, got.stderr)
	}
	for _, m := range jsonLines(t, got.stderr) {
		if m["level"] == "INFO" || m["level"] == "DEBUG" {
			t.Errorf("stderr line %v, want no info lines at log.level warn", m)
		}
	}
}

func TestImportCommand_InputDerivedLogAttributesAreQuoted(t *testing.T) {
	cfg := newServiceConfig(t)
	name := "a\u009bb\u202ec\x1bd\x7fe\nf.log"
	root := importDir(t, map[string][]byte{name: []byte("alpha 1\n"), "z-other" + name: []byte("beta\n")})

	got := runImport(t.Context(), t, cfg, alphaParsers(), root)

	if got.code != 0 {
		t.Fatalf("exit code = %d, want 0; stderr: %s", got.code, got.stderr)
	}
	for _, raw := range []string{"\xc2\x9b", "\xe2\x80\xae", "\x7f", "\x1b"} {
		if strings.Contains(got.stderr, raw) {
			t.Errorf("stderr holds the raw bytes %q, want them escaped", raw)
		}
	}
	paths := map[string]map[string]any{}
	for _, m := range jsonLines(t, got.stderr) {
		if p, ok := m["path"].(string); ok && m["msg"] == "file finished" {
			paths[p] = m
		}
	}
	if m, ok := paths[strconv.Quote(name)]; !ok || m["outcome"] != "imported" {
		t.Errorf("file finished lines = %v, want one with path %s", paths, strconv.Quote(name))
	}
	if m, ok := paths[strconv.Quote("z-other"+name)]; !ok || m["reason"] != strconv.Quote("no parser recognized the file") {
		t.Errorf("file finished lines = %v, want one for the unrecognized file with the quoted reason %s", paths, strconv.Quote("no parser recognized the file"))
	}
}

func TestImportCommand_RunErrorTextIsQuotedInTheLog(t *testing.T) {
	cfg := newServiceConfig(t)
	missing := filepath.Join(t.TempDir(), "gone\u009b\u202e\x7f")

	got := runImport(t.Context(), t, cfg, alphaParsers(), missing)

	if got.code != 1 {
		t.Fatalf("exit code = %d, want 1", got.code)
	}
	for _, raw := range []string{"\xc2\x9b", "\xe2\x80\xae", "\x7f"} {
		if strings.Contains(got.stderr, raw) {
			t.Errorf("stderr holds the raw bytes %q, want them escaped", raw)
		}
	}
	lines := jsonLines(t, got.stderr)
	if len(lines) == 0 {
		t.Fatal("stderr is empty, want a JSON error line")
	}
	text, ok := lines[len(lines)-1]["error"].(string)
	if !ok || !strings.HasPrefix(text, `"`) || !strings.HasSuffix(text, `"`) || !strings.Contains(text, `\u009b`) {
		t.Errorf("error attribute = %q, want the error text quoted with the control characters escaped", text)
	}
}

func TestImportCommand_LogsHashingProgressOfALargeFile(t *testing.T) {
	cfg := newServiceConfig(t)
	root := importDir(t, nil)
	big := filepath.Join(root, "big.log")
	if err := os.WriteFile(big, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Truncate(big, importer.DefaultProgressBytes); err != nil {
		t.Fatal(err)
	}
	parsers := []logparse.Parser{&logparsetest.Parser{
		TypeName:   "syslog",
		DetectFunc: func(logparse.File, []byte) logparse.Confidence { return logparse.MatchContent },
		ParseFunc:  logparsetest.Lines("syslog", importFirst),
	}}

	got := runImport(t.Context(), t, cfg, parsers, root)

	if got.code != 0 {
		t.Fatalf("exit code = %d, want 0; stderr: %.500s", got.code, got.stderr)
	}
	var hashing []map[string]any
	for _, m := range jsonLines(t, got.stderr) {
		if m["msg"] == "hashing" {
			hashing = append(hashing, m)
		}
	}
	if len(hashing) != 1 {
		t.Fatalf("hashing lines = %d, want 1 for a file of %d bytes", len(hashing), importer.DefaultProgressBytes)
	}
	m := hashing[0]
	if m["level"] != "INFO" || m["path"] != strconv.Quote("big.log") || m["bytes"] != float64(importer.DefaultProgressBytes) {
		t.Errorf("hashing line = %v, want level INFO, path %s and bytes %d", m, strconv.Quote("big.log"), importer.DefaultProgressBytes)
	}
}
