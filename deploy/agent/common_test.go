package agent

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"time"
)

// The tests of this package source the install scripts into a bash process and call one function at a
// time. Every external command the functions use is a stub on PATH (the harness below); nothing needs root,
// the network or systemd. Rows of the tables that are about bytes (a BOM, a tab, a CR, the ASCII escape) are
// built from byte values, so no editor or formatter can change them.

const (
	commonScript  = "common.sh"
	installScript = "install.sh"

	// usageLine is the usage text of install.sh (plan.md, Approach, Output).
	usageLine = "usage: install.sh --version vX.Y.Z [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]"

	// testDeadline bounds one bash run so a function that waits for input fails the test instead of hanging.
	testDeadline = 60 * time.Second

	defaultSpool = "/var/lib/vandox/spool"
)

var (
	// asciiTab, asciiCR and asciiESC are control bytes of the fixtures.
	asciiTab = string(rune(9))
	asciiCR  = string(rune(13))
	asciiESC = string(rune(27))
	// asciiBackslash is the single backslash a case about an escape needs on disk.
	asciiBackslash = string(rune(92))
	// byteOrderMark, nextLine, lineSeparator and paragraphSeparator are the UTF-8 bytes of U+FEFF, U+0085,
	// U+2028 and U+2029.
	byteOrderMark      = string([]byte{0xef, 0xbb, 0xbf})
	nextLine           = string([]byte{0xc2, 0x85})
	lineSeparator      = string([]byte{0xe2, 0x80, 0xa8})
	paragraphSeparator = string([]byte{0xe2, 0x80, 0xa9})

	// toolNames are the real programs a function may use besides the stubs of a test; everything else
	// (curl, git, setpriv, getent, id, the user commands, plesk, mariadb) exists only as a stub.
	toolNames = []string{
		"awk", "basename", "cat", "chmod", "cp", "cut", "date", "dirname", "env", "find", "grep", "head", "ln",
		"ls", "mkdir", "mkfifo", "mktemp", "mv", "od", "readlink", "realpath", "rm", "sed", "sleep", "sort",
		"tail", "tee", "touch", "tr", "truncate", "uniq", "wc", "install", "true", "false", "test", "[",
	}
)

// shellResult is what one bash run printed and its exit status.
type shellResult struct {
	stdout string
	stderr string
	code   int
}

// harness is one scratch directory with stub programs, their logs and a bash that sources the scripts.
type harness struct {
	t     *testing.T
	dir   string
	stubs string
	logs  string
	tools string
	bash  string
	env   []string
}

// newHarness creates the scratch directory of a test with PATH holding the stub directory and the
// whitelisted real tools only.
func newHarness(t *testing.T) *harness {
	t.Helper()

	bash, err := exec.LookPath("bash")
	if err != nil {
		t.Fatalf("bash not found: %v", err)
	}

	h := &harness{t: t, dir: t.TempDir(), bash: bash}
	h.stubs = filepath.Join(h.dir, "stubs")
	h.logs = filepath.Join(h.dir, "logs")
	h.tools = filepath.Join(h.dir, "tools")

	for _, d := range []string{h.stubs, h.logs, h.tools} {
		if err := os.Mkdir(d, 0o755); err != nil {
			t.Fatalf("mkdir %s: %v", d, err)
		}
	}

	for _, name := range toolNames {
		found, err := exec.LookPath(name)
		if err != nil {
			continue
		}

		if err := os.Symlink(found, filepath.Join(h.tools, name)); err != nil {
			t.Fatalf("symlink tool %s: %v", name, err)
		}
	}

	return h
}

// setEnv adds one NAME=value pair to the environment of the bash process.
func (h *harness) setEnv(pairs ...string) {
	h.env = append(h.env, pairs...)
}

// path returns the PATH of the bash process: the stubs first, then the whitelisted tools.
func (h *harness) path() string {
	return h.stubs + ":" + h.tools
}

// run sources both scripts, then runs script with args as $1... and stdin on standard input.
func (h *harness) run(script, stdin string, args ...string) shellResult {
	h.t.Helper()

	return h.runWith(h.path(), script, stdin, args...)
}

// runWith is run with an explicit PATH value for the bash process.
func (h *harness) runWith(pathValue, script, stdin string, args ...string) shellResult {
	h.t.Helper()

	dirOfScripts, err := filepath.Abs(".")
	if err != nil {
		h.t.Fatalf("abs: %v", err)
	}

	full := "source " + shellQuote(filepath.Join(dirOfScripts, commonScript)) +
		"; source " + shellQuote(filepath.Join(dirOfScripts, installScript)) + "; set -euo pipefail; " + script

	ctx, cancel := context.WithTimeout(context.Background(), testDeadline)
	defer cancel()

	cmd := exec.CommandContext(ctx, h.bash, append([]string{"-c", full, installScript}, args...)...)
	cmd.Dir = h.dir
	cmd.Env = append([]string{
		"PATH=" + pathValue,
		"LC_ALL=C",
		"HOME=" + h.dir,
		"STUB_LOG=" + h.logs,
	}, h.env...)
	cmd.Stdin = strings.NewReader(stdin)

	var stdout, stderr bytes.Buffer

	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err = cmd.Run()
	code := 0

	if err != nil {
		var exitErr *exec.ExitError
		if errors.As(err, &exitErr) == false {
			h.t.Fatalf("run bash: %v", err)
		}

		code = exitErr.ExitCode()
	}

	if ctx.Err() != nil {
		h.t.Fatalf("bash did not finish within %s; script: %s", testDeadline, script)
	}

	return shellResult{stdout: stdout.String(), stderr: stderr.String(), code: code}
}

// shellQuote quotes s as one bash word.
func shellQuote(s string) string {
	return "'" + strings.ReplaceAll(s, "'", `'\''`) + "'"
}

// recordPreamble makes a stub log its arguments (one line per call, each in brackets) and its exported
// environment before it does anything else.
const recordPreamble = `#!/bin/bash
{ for a in "$@"; do printf '[%s]' "$a"; done; printf '\n'; } >>"$STUB_LOG/NAME.args"
{ declare -px; printf 'END\n'; } >>"$STUB_LOG/NAME.env"
`

// stub writes an executable stub that records its call and then runs body.
func (h *harness) stub(name, body string) {
	h.t.Helper()

	text := strings.ReplaceAll(recordPreamble, "NAME", name) + body + "\n"

	if err := os.WriteFile(filepath.Join(h.stubs, name), []byte(text), 0o755); err != nil {
		h.t.Fatalf("write stub %s: %v", name, err)
	}
}

// passthrough writes a stub that records its call and then runs the real program of the same name.
func (h *harness) passthrough(name string) {
	h.t.Helper()

	found, err := exec.LookPath(name)
	if err != nil {
		h.t.Fatalf("real %s not found: %v", name, err)
	}

	h.stub(name, "exec "+shellQuote(found)+` "$@"`)
}

// calls returns the recorded argument lines of a stub, one per call, each as "[a][b]".
func (h *harness) calls(name string) []string {
	h.t.Helper()

	data, err := os.ReadFile(filepath.Join(h.logs, name+".args"))
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}

	if err != nil {
		h.t.Fatalf("read calls of %s: %v", name, err)
	}

	return strings.Split(strings.TrimSuffix(string(data), "\n"), "\n")
}

// allLogs returns every recorded argument and environment line of every stub, for leak checks.
func (h *harness) allLogs() string {
	h.t.Helper()

	entries, err := os.ReadDir(h.logs)
	if err != nil {
		h.t.Fatalf("read logs: %v", err)
	}

	var sb strings.Builder

	for _, e := range entries {
		data, err := os.ReadFile(filepath.Join(h.logs, e.Name()))
		if err != nil {
			h.t.Fatalf("read log %s: %v", e.Name(), err)
		}

		sb.WriteString(e.Name() + ":\n" + string(data))
	}

	return sb.String()
}

// writeFile writes a file below the scratch directory and returns its path.
func (h *harness) writeFile(rel, content string) string {
	h.t.Helper()

	path := filepath.Join(h.dir, rel)

	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		h.t.Fatalf("mkdir for %s: %v", rel, err)
	}

	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		h.t.Fatalf("write %s: %v", rel, err)
	}

	return path
}

// expectResult compares exit status, standard output and standard error exactly.
func expectResult(t *testing.T, what string, got shellResult, code int, stdout, stderr string) {
	t.Helper()

	if got.code != code {
		t.Errorf("%s: exit status = %d, want %d (stdout %q, stderr %q)", what, got.code, code, got.stdout, got.stderr)
	}

	if got.stdout != stdout {
		t.Errorf("%s: standard output = %q, want %q", what, got.stdout, stdout)
	}

	if got.stderr != stderr {
		t.Errorf("%s: standard error = %q, want %q", what, got.stderr, stderr)
	}
}

// fileMode returns the permission bits of a file or directory without following a link.
func fileMode(t *testing.T, path string) os.FileMode {
	t.Helper()

	info, err := os.Lstat(path)
	if err != nil {
		t.Fatalf("lstat %s: %v", path, err)
	}

	return info.Mode().Perm()
}

// shippedFile returns the path of a file shipped in this package's directory.
func shippedFile(t *testing.T, name string) string {
	t.Helper()

	path, err := filepath.Abs(name)
	if err != nil {
		t.Fatalf("abs %s: %v", name, err)
	}

	if _, err := os.Stat(path); err != nil {
		t.Fatalf("stat %s: %v", path, err)
	}

	return path
}

// spoolConfig builds an agent.yaml whose spool block holds the given (already indented) lines.
func spoolConfig(lines ...string) string {
	return "agent_id: web-1\nbackend:\n  url: http://100.64.0.1:8081\nspool:\n" + strings.Join(lines, "\n") + "\n"
}

// spoolMessageKind says which error line a spool case expects.
type spoolMessageKind int

const (
	spoolOK     spoolMessageKind = iota // the path is printed
	spoolCannot                         // cannot read spool.directory
	spoolValue                          // spool.directory must be one directory below /var/lib/vandox
	spoolEither                         // either of the two lines (the plan does not fix the class)
)

var (
	spoolCannotRE = regexp.MustCompile(`^(install|uninstall)\.sh: cannot read spool\.directory from \S+\n$`)
	spoolValueRE  = regexp.MustCompile(`^(install|uninstall)\.sh: spool\.directory must be one directory below /var/lib/vandox\n$`)
)

// spoolCase is one agent.yaml and the answer of read_spool_directory.
type spoolCase struct {
	name    string
	content string
	file    string // when set, the shipped file of this name is read instead of content
	kind    spoolMessageKind
	want    string // the path printed, for spoolOK
	must    string // a byte sequence the fixture must still hold
}

// spoolOKCases are the files read_spool_directory accepts.
func spoolOKCases() []spoolCase {
	dir := func(v string) string { return spoolConfig("  directory: " + v) }

	return []spoolCase{
		{name: "no spool section", content: "agent_id: web-1\nbackend:\n  url: http://100.64.0.1:8081\n", want: defaultSpool},
		{name: "empty file", content: "", want: defaultSpool},
		{name: "only comments and blank lines", content: "# one\n\n  # two\n", want: defaultSpool},
		{name: "block without directory", content: spoolConfig("  max_size: 2GiB"), want: defaultSpool},
		{name: "plain value", content: dir("/var/lib/vandox/data"), want: "/var/lib/vandox/data"},
		{name: "single-quoted value", content: dir("'/var/lib/vandox/data'"), want: "/var/lib/vandox/data"},
		{name: "double-quoted value", content: dir(`"/var/lib/vandox/data"`), want: "/var/lib/vandox/data"},
		{name: "trailing comment", content: dir("/var/lib/vandox/data # where"), want: "/var/lib/vandox/data"},
		{name: "trailing comment after a quoted value", content: dir(`"/var/lib/vandox/data" # where`), want: "/var/lib/vandox/data"},
		{
			name:    "other keys, comments and blank lines in the block",
			content: spoolConfig("  # the spool", "", "  max_size: 2GiB", "  directory: /var/lib/vandox/data", "  other_key: 5 # c"),
			want:    "/var/lib/vandox/data",
		},
		{name: "comment after spool", content: "spool: # the spool\n  directory: /var/lib/vandox/data\n", want: "/var/lib/vandox/data"},
		{name: "name with dot underscore and dash", content: dir("/var/lib/vandox/My_dir-1.2"), want: "/var/lib/vandox/My_dir-1.2"},
		{name: "name starting with a dot", content: dir("/var/lib/vandox/.hidden"), want: "/var/lib/vandox/.hidden"},
		{name: "name of three dots", content: dir("/var/lib/vandox/..."), want: "/var/lib/vandox/..."},
		{
			name:    "directory key of another block is not the spool's",
			content: "spool:\n  max_size: 2GiB\nother:\n  directory: /tmp/x\n",
			want:    defaultSpool,
		},
		{
			name:    "spool block followed by another top-level key",
			content: "spool:\n  directory: /var/lib/vandox/data\nlogs:\n  directory: /tmp/x\n",
			want:    "/var/lib/vandox/data",
		},
		{name: "the shipped example agent.yaml (N11)", file: "agent.yaml", want: defaultSpool},
	}
}

// spoolValueCases are files whose spool.directory value is refused with the line of E6.
func spoolValueCases() []spoolCase {
	dir := func(v string) string { return spoolConfig("  directory: " + v) }
	cases := []spoolCase{}

	for _, v := range []string{
		"/var/lib/vandox", "/var/lib/vandox/", "/var/lib/vandox/a/b", "/var/lib/vandox/..", "/var/lib/vandox/.",
		"/var/lib/vandox/a b", "/var/lib/vandox2/x", "/tmp/x", "/var/lib/vandox/x/", "//", "var/lib/vandox/x",
		"/var/lib/vandox//x", "/var/lib/vandox/a$b",
	} {
		cases = append(cases, spoolCase{name: "value " + v, content: dir(v), kind: spoolValue})
	}

	cases = append(cases,
		spoolCase{name: "quoted value outside", content: dir("'/tmp/x'"), kind: spoolValue},
		spoolCase{name: "double-quoted value with a space", content: dir(`"/var/lib/vandox/a b"`), kind: spoolValue},
		spoolCase{name: "hash without a space before it", content: dir("/var/lib/vandox/x#c"), kind: spoolEither},
	)

	return cases
}

// spoolSyntaxCases are files read_spool_directory refuses as unreadable (fail closed).
func spoolSyntaxCases() []spoolCase {
	dir := func(v string) string { return spoolConfig("  directory: " + v) }
	cases := []spoolCase{
		{name: "flow mapping after spool", content: "spool: {directory: /tmp/d}\n", kind: spoolCannot},
		{name: "flow mapping as value", content: dir("{a: b}"), kind: spoolCannot},
		{name: "flow sequence as value", content: dir("[a]"), kind: spoolCannot},
		{name: "two directory keys", content: spoolConfig("  directory: /var/lib/vandox/a", "  directory: /var/lib/vandox/b"), kind: spoolCannot},
		{name: "tab as indentation", content: "spool:\n" + asciiTab + "directory: /var/lib/vandox/x\n", kind: spoolCannot},
		{name: "tab before a comment", content: dir("/var/lib/vandox/x" + asciiTab + "# c"), kind: spoolCannot},
		{name: "byte order mark", content: byteOrderMark + "agent_id: x\n", kind: spoolCannot},
		{name: "directive line", content: "%YAML 1.2\nagent_id: x\n", kind: spoolCannot},
		{name: "document start line", content: "---\nagent_id: x\n", kind: spoolCannot},
		{name: "document end line", content: "agent_id: x\n...\n", kind: spoolCannot},
		{name: "carriage return", content: "spool:" + asciiCR + "\n  directory: /var/lib/vandox/x" + asciiCR + "\n", kind: spoolCannot},
		{name: "double-quoted key", content: spoolConfig(`  "directory": /tmp/x`), kind: spoolCannot},
		{name: "single-quoted key", content: spoolConfig(`  'directory': /tmp/x`), kind: spoolCannot},
		{name: "question-mark key", content: spoolConfig("  ? directory", "  : /tmp/x"), kind: spoolCannot},
		{name: "merge key", content: spoolConfig("  <<: *x"), kind: spoolCannot},
		{name: "anchor on the value", content: dir("&a /var/lib/vandox/x"), kind: spoolCannot},
		{name: "alias as the value", content: dir("*a"), kind: spoolCannot},
		{name: "tag on the value", content: dir("!!str /var/lib/vandox/x"), kind: spoolCannot},
		{name: "literal block scalar", content: spoolConfig("  directory: |", "    /var/lib/vandox/x"), kind: spoolCannot},
		{name: "folded block scalar", content: spoolConfig("  directory: >", "    /var/lib/vandox/x"), kind: spoolCannot},
		{name: "value on the next line", content: spoolConfig("  directory:", "    /var/lib/vandox/x"), kind: spoolCannot},
		{name: "continued plain scalar", content: spoolConfig("  directory: /var/lib/vandox/x", "    more"), kind: spoolCannot},
		{name: "directory at indent 4", content: spoolConfig("    directory: /var/lib/vandox/x"), kind: spoolCannot},
		{name: "directory at indent 1", content: spoolConfig(" directory: /var/lib/vandox/x"), kind: spoolCannot},
		{name: "doubled single quote inside single quotes", content: dir("'/var/lib/vandox/it''s'"), kind: spoolCannot},
		{
			name:    "backslash inside double quotes",
			content: dir(`"/var/lib/vandox/a` + asciiBackslash + `b"`),
			kind:    spoolCannot,
			must:    asciiBackslash,
		},
		{name: "spool followed by a value", content: "spool: x\n", kind: spoolCannot},
		{name: "double-quoted spool key at column 0", content: "\"spool\":\n  directory: /tmp/d\n", kind: spoolCannot},
		{name: "single-quoted spool key at column 0", content: "'spool':\n  directory: /tmp/d\n", kind: spoolCannot},
		{name: "space before the colon of spool", content: "spool :\n  directory: /tmp/d\n", kind: spoolCannot},
		{name: "question-mark spool key", content: "? spool\n: {directory: /tmp/d}\n", kind: spoolCannot},
		{name: "whole file as a flow mapping", content: "{spool: {directory: /tmp/d}}\n", kind: spoolCannot},
		{name: "next line character inside a comment", content: spoolConfig("  # a" + nextLine + "  directory: /tmp/x"), kind: spoolCannot},
		{name: "line separator inside a comment", content: spoolConfig("  # a" + lineSeparator + "  directory: /tmp/x"), kind: spoolCannot},
		{name: "paragraph separator inside a comment", content: spoolConfig("  # a" + paragraphSeparator + "  directory: /tmp/x"), kind: spoolCannot},
		{name: "UTF-8 letter in a comment", content: "# caf" + string([]byte{0xc3, 0xa9}) + "\nspool:\n  directory: /var/lib/vandox/x\n", kind: spoolCannot},
		{name: "unknown line at column 0", content: "agent_id: x\n- item\n", kind: spoolCannot},
		{name: "capital letter in a top-level key", content: "Spool:\n  directory: /tmp/x\n", kind: spoolCannot},
	}

	return cases
}

// TestReadSpoolDirectory_AcceptsOnlyOneDirectoryBelowVarLibVandox runs every input form of the fail-closed
// reader (plan.md, Approach, read_spool_directory), including the shipped example file (N11).
func TestReadSpoolDirectory_AcceptsOnlyOneDirectoryBelowVarLibVandox(t *testing.T) {
	t.Parallel()

	all := append(append(spoolOKCases(), spoolValueCases()...), spoolSyntaxCases()...)

	for _, tc := range all {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)

			path := h.writeFile("agent.yaml", tc.content)
			if tc.file != "" {
				path = shippedFile(t, tc.file)
			}

			if tc.must != "" && strings.Contains(tc.content, tc.must) == false {
				t.Fatalf("fixture lost the byte sequence %q", tc.must)
			}

			got := h.run(`read_spool_directory "$1"`, "", path)

			assertSpoolResult(t, tc, got)
		})
	}
}

// assertSpoolResult compares the answer of read_spool_directory with the case.
func assertSpoolResult(t *testing.T, tc spoolCase, got shellResult) {
	t.Helper()

	if tc.kind == spoolOK {
		expectResult(t, "read_spool_directory", got, 0, tc.want+"\n", "")

		return
	}

	if got.code != 2 {
		t.Errorf("read_spool_directory: exit status = %d, want 2 (stdout %q, stderr %q)", got.code, got.stdout, got.stderr)
	}

	if got.stdout != "" {
		t.Errorf("read_spool_directory: standard output = %q, want it empty", got.stdout)
	}

	matches := map[spoolMessageKind]bool{
		spoolCannot: spoolCannotRE.MatchString(got.stderr),
		spoolValue:  spoolValueRE.MatchString(got.stderr),
		spoolEither: spoolCannotRE.MatchString(got.stderr) || spoolValueRE.MatchString(got.stderr),
	}

	if matches[tc.kind] == false {
		t.Errorf("read_spool_directory: standard error = %q, want the line for kind %d", got.stderr, tc.kind)
	}
}

// TestCheckPlatform_RefusesNonRootNonSystemdAndOtherMachines covers E1: the first failing precondition
// decides, in the order root, systemd, machine.
func TestCheckPlatform_RefusesNonRootNonSystemdAndOtherMachines(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name    string
		euid    string
		pid1    string
		machine string
		want    string
	}{
		{"root on systemd and x86_64", "0", "systemd", "x86_64", ""},
		{"not root", "1000", "systemd", "x86_64", "install.sh: must run as root"},
		{"not root and not systemd", "1000", "init", "aarch64", "install.sh: must run as root"},
		{"init as PID 1", "0", "init", "x86_64", "install.sh: needs systemd"},
		{"another name starting with systemd", "0", "systemd-foo", "x86_64", "install.sh: needs systemd"},
		{"empty PID 1 name", "0", "", "x86_64", "install.sh: needs systemd"},
		{"not systemd and another machine", "0", "init", "aarch64", "install.sh: needs systemd"},
		{"another machine", "0", "systemd", "aarch64", "install.sh: only linux-amd64 is supported"},
		{"machine with a suffix", "0", "systemd", "x86_64-x", "install.sh: only linux-amd64 is supported"},
		{"empty machine", "0", "systemd", "", "install.sh: only linux-amd64 is supported"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.run(`check_platform "$1" "$2" "$3"`, "", tc.euid, tc.pid1, tc.machine)

			if tc.want == "" {
				expectResult(t, "check_platform", got, 0, "", "")

				return
			}

			expectResult(t, "check_platform", got, 1, "", tc.want+"\n")
		})
	}
}

// sqlFileName is the shipped MariaDB account script, which the install script runs unchanged.
const sqlFileName = "mariadb-user.sql"

// mariadbWarningRE builds the E7 warning line of an account tool.
func mariadbWarningRE(tool string) *regexp.Regexp {
	return regexp.MustCompile(`^install\.sh: WARNING MariaDB account not created; run: ` + regexp.QuoteMeta(tool) +
		` < (\S+/)?deploy/agent/mariadb-user\.sql\n$`)
}

// TestMariadbAccount_UsesPleskWhenPresent checks that plesk db gets the SQL file on standard input and that
// mariadb is not used when plesk is on the search path.
func TestMariadbAccount_UsesPleskWhenPresent(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	sqlPath := shippedFile(t, sqlFileName)

	h.stub("plesk", `cat >"$STUB_LOG/plesk.stdin"`)
	h.stub("mariadb", `cat >"$STUB_LOG/mariadb.stdin"`)

	got := h.run(`run_mariadb_sql "$1" install.sh`, "", sqlPath)

	expectResult(t, "run_mariadb_sql with plesk", got, 0, "", "")

	if calls := h.calls("plesk"); len(calls) != 1 || calls[0] != "[db]" {
		t.Errorf("plesk calls = %q, want exactly [db]", calls)
	}

	if calls := h.calls("mariadb"); len(calls) != 0 {
		t.Errorf("mariadb calls = %q, want none when plesk is present", calls)
	}

	want, err := os.ReadFile(sqlPath)
	if err != nil {
		t.Fatalf("read %s: %v", sqlPath, err)
	}

	stdin, err := os.ReadFile(filepath.Join(h.logs, "plesk.stdin"))
	if err != nil {
		t.Fatalf("plesk got no standard input: %v", err)
	}

	if string(stdin) != string(want) {
		t.Errorf("standard input of plesk db = %q, want the content of %s (%q)", stdin, sqlFileName, want)
	}
}

// TestMariadbAccount_WarnsAndContinuesWhenItFails checks E7: a failing administrator login prints the
// warning with the command to run and the function still succeeds.
func TestMariadbAccount_WarnsAndContinuesWhenItFails(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name string
		tool string
		warn string
	}{
		{"plesk fails", "plesk", "plesk db"},
		{"mariadb fails", "mariadb", "mariadb"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			sqlPath := shippedFile(t, sqlFileName)

			h.stub(tc.tool, `cat >/dev/null; echo "ERROR 1045 (28000): Access denied" >&2; exit 1`)

			got := h.run(`run_mariadb_sql "$1" install.sh`, "", sqlPath)

			if got.code != 0 {
				t.Errorf("run_mariadb_sql: exit status = %d, want 0 (the install continues)", got.code)
			}

			if mariadbWarningRE(tc.warn).MatchString(got.stdout) == false {
				t.Errorf("run_mariadb_sql: standard output = %q, want the E7 warning for %q", got.stdout, tc.warn)
			}
		})
	}
}

// TestMariadbAccount_SkippedWithNoMariadb checks E7 for a server without MariaDB and without Plesk: no
// command can run, the warning names mariadb, and the function still succeeds.
func TestMariadbAccount_SkippedWithNoMariadb(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	sqlPath := shippedFile(t, sqlFileName)

	got := h.run(`run_mariadb_sql "$1" install.sh`, "", sqlPath)

	if got.code != 0 {
		t.Errorf("run_mariadb_sql: exit status = %d, want 0 (the install continues)", got.code)
	}

	if mariadbWarningRE("mariadb").MatchString(got.stdout) == false {
		t.Errorf("run_mariadb_sql: standard output = %q, want the E7 warning for mariadb", got.stdout)
	}
}
