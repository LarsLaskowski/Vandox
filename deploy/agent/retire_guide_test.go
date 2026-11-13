package agent

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"testing"
)

const (
	// retireGuideFile is the retirement guide, relative to the repository root.
	retireGuideFile = "docs/RETIRE_LEGACY_MONITORING.md"

	// installFile is the installation guide, relative to the repository root.
	installFile = "docs/INSTALL.md"

	// scriptCronLine is the one crontab line the retirement removes.
	scriptCronLine = "* * * * * /usr/local/bin/syslog.sh"

	// retireLoginName is the login the stub getent knows and the tests give as SUDO_USER.
	retireLoginName = "operator"
)

// packNamePattern finds the archive name in the form NB6 of the security review pins: "${home:?}/<name>.tar.gz".
var packNamePattern = regexp.MustCompile(`"\$\{home:\?\}/([^"/$]+\.tar\.gz)"`)

// Stubs of the commands the documented server lines call; each records into $STUB_LOG.
const (
	retireGetentStub = `#!/bin/bash
[[ $1 == passwd && $2 == operator ]] || exit 2
printf 'operator:x:1000:1000::%s:/bin/bash\n' "$STUB_HOME"
`
	retireTarStub = `#!/bin/bash
pwd >"$STUB_LOG/tar.cwd"
printf '%s\n' "$@" >"$STUB_LOG/tar.args"
printf 'ARCHIVE'
`
	retireRunuserStub = `#!/bin/bash
printf '%s\n' "$@" >>"$STUB_LOG/runuser.args"
[[ $1 == -u ]] || exit 64
shift 2
[[ $1 == -- ]] && shift
exec "$@"
`
	retireCrontabStub = `#!/bin/bash
case $1 in
-l)
    [[ -e "$STUB_STATE.fail" ]] && exit 1
    [[ -e "$STUB_STATE" ]] || exit 1
    cat "$STUB_STATE"
    ;;
-e)
    echo edit >>"$STUB_LOG/crontab.edits"
    ;;
*)
    src=$1
    [[ $src == - ]] && src=/dev/stdin
    content=$(cat -- "$src"; printf x) || exit 1
    printf '%s' "${content%x}" >"$STUB_STATE"
    echo install >>"$STUB_LOG/crontab.installs"
    ;;
esac
`
)

// guideLine is one line of a fenced block of the guide with the index of its block.
type guideLine struct {
	block int
	text  string
}

// retireGuideLines returns the lines of every fenced block of the guide in document order.
func retireGuideLines(t *testing.T) []guideLine {
	t.Helper()

	var lines []guideLine
	for i, block := range fencedBlocks(readRepositoryText(t, retireGuideFile)) {
		for _, line := range block {
			lines = append(lines, guideLine{block: i, text: line})
		}
	}

	return lines
}

// indexesOf returns the indexes of the lines that contain needle.
func indexesOf(lines []guideLine, needle string) []int {
	var found []int
	for i, line := range lines {
		if strings.Contains(line.text, needle) {
			found = append(found, i)
		}
	}

	return found
}

// onlyLine returns the one line of the guide that holds needle and fails when there is none or more than one.
func onlyLine(t *testing.T, needle string) string {
	t.Helper()

	lines := retireGuideLines(t)

	found := indexesOf(lines, needle)
	if len(found) != 1 {
		t.Fatalf("the guide has %d fenced lines holding %q, want exactly 1", len(found), needle)
	}

	return lines[found[0]].text
}

// retireEnv is the scratch machine a documented server line runs on: stub commands first on PATH, a directory
// for /root, one for /var/log, a login home, and a working directory that holds files.
type retireEnv struct {
	t     *testing.T
	root  string
	logs  string
	home  string
	work  string
	stubs string
	calls string
	state string
}

// newRetireEnv builds the scratch machine of a test.
func newRetireEnv(t *testing.T) *retireEnv {
	t.Helper()

	dir := t.TempDir()
	e := &retireEnv{
		t: t, root: filepath.Join(dir, "root"), logs: filepath.Join(dir, "varlog"), home: filepath.Join(dir, "home", retireLoginName),
		work: filepath.Join(dir, "work"), stubs: filepath.Join(dir, "stubs"), calls: filepath.Join(dir, "calls"), state: filepath.Join(dir, "crontab.state"),
	}

	for _, d := range []string{e.root, e.logs, e.home, e.work, e.stubs, e.calls} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatalf("mkdir %s: %v", d, err)
		}
	}

	for name, text := range map[string]string{"getent": retireGetentStub, "tar": retireTarStub, "runuser": retireRunuserStub, "crontab": retireCrontabStub} {
		e.write(filepath.Join(e.stubs, name), text, 0o755)
	}

	return e
}

// write writes a file of the scratch machine.
func (e *retireEnv) write(path, content string, mode os.FileMode) {
	e.t.Helper()

	if err := os.WriteFile(path, []byte(content), mode); err != nil {
		e.t.Fatalf("write %s: %v", path, err)
	}
}

// read returns a file of the scratch machine, or the marker (absent) when it does not exist.
func (e *retireEnv) read(path string) string {
	e.t.Helper()

	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return "(absent)"
	}

	if err != nil {
		e.t.Fatalf("read %s: %v", path, err)
	}

	return string(data)
}

// fill puts files into the working directory, among them names the patterns of the lines could match.
func (e *retireEnv) fill() {
	e.t.Helper()

	for _, name := range []string{"sysmonitor.log.old", "sysmonitor.log", "alpha", "beta", "1"} {
		e.write(filepath.Join(e.work, name), "work\n", 0o644)
	}
}

// substitute points the absolute paths of a server line at the scratch machine.
func (e *retireEnv) substitute(line string) string {
	return strings.NewReplacer("/var/log", e.logs, "/root/", e.root+"/").Replace(line)
}

// result is the outcome of a run of bash.
type retireResult struct {
	code   int
	stdout string
	stderr string
}

// run runs a script with bash in the working directory; withLogin sets SUDO_USER, and home is what getent reports.
func (e *retireEnv) run(script string, withLogin bool, home string) retireResult {
	e.t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), testDeadline)
	defer cancel()

	cmd := exec.CommandContext(ctx, "/bin/bash", "-c", script)
	cmd.Dir = e.work
	cmd.Env = []string{
		"PATH=" + e.stubs + ":/usr/bin:/bin", "LC_ALL=C", "HOME=" + e.work,
		"STUB_LOG=" + e.calls, "STUB_STATE=" + e.state, "STUB_HOME=" + home,
	}

	if withLogin {
		cmd.Env = append(cmd.Env, "SUDO_USER="+retireLoginName)
	}

	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr

	err := cmd.Run()
	if ctx.Err() != nil {
		e.t.Fatalf("the documented line did not finish within %s", testDeadline)
	}

	var exitErr *exec.ExitError
	code := 0
	if errors.As(err, &exitErr) {
		code = exitErr.ExitCode()
	} else if err != nil {
		e.t.Fatalf("run bash: %v", err)
	}

	return retireResult{code: code, stdout: stdout.String(), stderr: stderr.String()}
}

// installs returns how often the stub crontab installed a table.
func (e *retireEnv) installs() int {
	e.t.Helper()

	got := e.read(filepath.Join(e.calls, "crontab.installs"))
	if got == "(absent)" {
		return 0
	}

	return strings.Count(got, "install")
}

// setCrontab sets the table the stub crontab holds.
func (e *retireEnv) setCrontab(table string) {
	e.t.Helper()

	e.write(e.state, table, 0o600)
}

// savedBefore and savedFiltered are the two files the retirement line keeps.
func (e *retireEnv) savedBefore() string {
	return filepath.Join(e.root, "crontab.before-vandox")
}

func (e *retireEnv) savedFiltered() string {
	return filepath.Join(e.root, "crontab.vandox-filtered")
}

// packLines returns, for each fenced line holding the pack command, the script that runs it: the nearest earlier
// `getent passwd` line and the pack line, one per line as pasted at once.
func packScripts(t *testing.T) []string {
	t.Helper()

	lines := retireGuideLines(t)

	var scripts []string
	for _, i := range indexesOf(lines, "tar -czf -") {
		home := ""
		for j := i - 1; j >= 0; j-- {
			if strings.Contains(lines[j].text, "getent passwd") {
				home = lines[j].text
				break
			}
		}

		if home == "" {
			t.Fatalf("no `getent passwd` line before the pack line %q, want the login's home read from it", lines[i].text)
		}

		scripts = append(scripts, home+"\n"+lines[i].text)
	}

	return scripts
}

// archiveName returns the archive name the pack script writes.
func archiveName(t *testing.T, script string) string {
	t.Helper()

	match := packNamePattern.FindStringSubmatch(script)
	if match == nil {
		t.Fatalf("pack script %q lacks the form \"${home:?}/<name>.tar.gz\", want the home checked non-empty by :?", script)
	}

	return match[1]
}

func TestRetireGuide_ComparisonNamesEveryKind_AC2(t *testing.T) {
	t.Parallel()

	guide := readRepositoryText(t, retireGuideFile)

	var rows []string
	for _, line := range strings.Split(guide, "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), "|") {
			rows = append(rows, collapse(line))
		}
	}

	for _, kind := range []string{"top", "process table", "meminfo", "pressure", "vmstat", "lsof -ni"} {
		var holding int
		for _, row := range rows {
			if strings.Contains(row, kind) {
				holding++
			}
		}

		if holding == 0 {
			t.Errorf("AC2 the guide's table has no row naming %q, want one row for each kind of data the script writes", kind)
		}
	}

	text := collapse(guide)
	containsAll(t, text, "the guide's rule when a comparison item fails (E1)", "do not retire", "issue")
	containsAll(t, text, "the guide's links to the area documents", "areas/")

	install := readRepositoryText(t, installFile)
	step11 := strings.Index(install, "## 11. ")
	link := strings.Index(install, "RETIRE_LEGACY_MONITORING.md")

	if step11 < 0 || link < step11 {
		t.Errorf("AC2 %s links the guide at %d, step 11 starts at %d, want the link after step 11", installFile, link, step11)
	}
}

func TestRetireGuide_ArchiveHoldsOnlyTheScriptLog_AC3(t *testing.T) {
	t.Parallel()

	for _, block := range fencedBlocks(readRepositoryText(t, retireGuideFile)) {
		if strings.Contains(strings.Join(block, "\n"), "journalctl") {
			t.Errorf("AC3 a fenced block of the guide holds `journalctl`, want the script's log alone: %q", block)
		}
	}

	scripts := packScripts(t)
	if len(scripts) != 2 {
		t.Fatalf("AC3 the guide has %d pack lines, want 2 (the archive of step 1 and the one of step 3)", len(scripts))
	}

	if archiveName(t, scripts[0]) == archiveName(t, scripts[1]) {
		t.Errorf("AC3 both pack lines write %q, want different archive names", archiveName(t, scripts[0]))
	}

	for i, script := range scripts {
		containsAll(t, script, "the pack script", `${SUDO_USER:?}`, "set -o pipefail", "umask 077", "runuser")
		runPackScript(t, i, script)
	}
}

// runPackScript runs one pack script on scratch machines and checks what tar and runuser got and what was written.
func runPackScript(t *testing.T, i int, script string) {
	t.Helper()

	label := strconv.Itoa(i + 1)

	t.Run("AC3 the pack line archives only the script log "+label, func(t *testing.T) {
		checkPackArchivesOnlyTheLog(t, script)
	})

	t.Run("AC3 the pack line runs nothing without a login or a home "+label, func(t *testing.T) {
		checkPackRunsNothing(t, script)
	})
}

// checkPackArchivesOnlyTheLog runs the pack script with a login and checks the tar arguments and the archive.
func checkPackArchivesOnlyTheLog(t *testing.T, script string) {
	t.Helper()

	e := newRetireEnv(t)
	e.fill()

	for _, n := range []string{"sysmonitor.log", "sysmonitor.log.1.gz", "sysmonitor.log.14.gz", "syslog", "auth.log"} {
		e.write(filepath.Join(e.logs, n), "log\n", 0o640)
	}

	res := e.run(e.substitute(script), true, e.home)
	if res.code != 0 {
		t.Fatalf("pack line exit = %d, want 0; stderr %q", res.code, res.stderr)
	}

	if cwd := strings.TrimSpace(e.read(filepath.Join(e.calls, "tar.cwd"))); cwd != e.logs {
		t.Errorf("tar ran in %q, want the log directory %q", cwd, e.logs)
	}

	var names []string
	for _, arg := range strings.Split(strings.TrimSpace(e.read(filepath.Join(e.calls, "tar.args"))), "\n") {
		if strings.HasPrefix(arg, "-") == false {
			names = append(names, arg)
		}
	}

	sort.Strings(names)

	want := []string{"sysmonitor.log", "sysmonitor.log.1.gz", "sysmonitor.log.14.gz"}
	if strings.Join(names, " ") != strings.Join(want, " ") {
		t.Errorf("tar was given %q, want exactly %q", names, want)
	}

	if runuser := e.read(filepath.Join(e.calls, "runuser.args")); strings.Contains(runuser, "-u\n"+retireLoginName+"\n") == false {
		t.Errorf("runuser got %q, want it to run as the login %q", runuser, retireLoginName)
	}

	archive := filepath.Join(e.home, archiveName(t, script))
	if got := e.read(archive); got != "ARCHIVE" {
		t.Errorf("archive %s = %q, want the stream of tar written by the login", archive, got)
	}

	if info, err := os.Stat(archive); err == nil && info.Mode().Perm() != 0o600 {
		t.Errorf("archive mode = %o, want 600", info.Mode().Perm())
	}
}

// checkPackRunsNothing runs the pack script without SUDO_USER and with an empty home: it must fail, start no
// runuser and leave no archive anywhere.
func checkPackRunsNothing(t *testing.T, script string) {
	t.Helper()

	cases := []struct {
		what      string
		withLogin bool
	}{
		{"SUDO_USER unset", false},
		{"an empty home", true},
	}

	for _, tc := range cases {
		e := newRetireEnv(t)
		e.fill()
		e.write(filepath.Join(e.logs, "sysmonitor.log"), "log\n", 0o640)

		if res := e.run(e.substitute(script), tc.withLogin, ""); res.code == 0 {
			t.Errorf("%s: pack line exit = 0, want a failure", tc.what)
		}

		if got := e.read(filepath.Join(e.calls, "runuser.args")); got != "(absent)" {
			t.Errorf("%s: runuser ran with %q, want nothing to run", tc.what, got)
		}

		for _, dir := range []string{e.work, e.home, filepath.Dir(e.home), e.root} {
			if matches, _ := filepath.Glob(filepath.Join(dir, "*.tar.gz")); len(matches) != 0 {
				t.Errorf("%s: archives %q written in %s, want none", tc.what, matches, dir)
			}
		}
	}
}

// cronCase is one crontab the retirement line is run against.
type cronCase struct {
	name     string
	table    string
	failList bool
	want     string // the table installed, empty for none
}

func retireCrontabCases() []cronCase {
	others := "MAILTO=root\n*/10 * * * * /usr/local/psa/admin/bin/psaadm-job\n0 3 * * * /usr/local/psa/admin/bin/psaadm-nightly\n"

	return []cronCase{
		{name: "only the exact line leaves", table: others[:len("MAILTO=root\n")] + scriptCronLine + "\n" + others[len("MAILTO=root\n"):], want: others},
		{name: "the line at the end leaves", table: others + scriptCronLine + "\n", want: others},
		{name: "a line with a redirect is not the line", table: others + scriptCronLine + " >/dev/null 2>&1\n"},
		{name: "no script line", table: others},
		{name: "a crontab holding only the script line", table: scriptCronLine + "\n"},
		{name: "the line twice", table: others + scriptCronLine + "\n" + scriptCronLine + "\n"},
		{name: "a failing crontab -l", table: others + scriptCronLine + "\n", failList: true},
	}
}

func TestRetireGuide_RetirementRemovesOnlyTheScriptLine_AC3(t *testing.T) {
	t.Parallel()

	line := onlyLine(t, "grep -vxF")
	containsAll(t, line, "the retirement line", "set -C", "umask 077", "crontab.before-vandox", "crontab.vandox-filtered")

	for _, tc := range retireCrontabCases() {
		t.Run("AC3 "+tc.name, func(t *testing.T) {
			e := newRetireEnv(t)
			e.fill()
			e.setCrontab(tc.table)

			if tc.failList {
				e.write(e.state+".fail", "", 0o600)
			}

			e.run(e.substitute(line), true, e.home)
			assertRetired(t, e, tc)
		})
	}
}

// assertRetired checks what the retirement line installed and saved for a crontab case.
func assertRetired(t *testing.T, e *retireEnv, tc cronCase) {
	t.Helper()

	if tc.want == "" {
		if n := e.installs(); n != 0 {
			t.Errorf("crontab installed %d times, want nothing installed; table now %q", n, e.read(e.state))
		}

		return
	}

	if n := e.installs(); n != 1 {
		t.Fatalf("crontab installed %d times, want once", n)
	}

	if got := e.read(e.state); got != tc.want {
		t.Errorf("installed crontab = %q, want %q (only the script line removed)", got, tc.want)
	}

	if got := e.read(e.savedBefore()); got != tc.table {
		t.Errorf("%s = %q, want the crontab of before %q", e.savedBefore(), got, tc.table)
	}

	if got := e.read(e.savedFiltered()); got != tc.want {
		t.Errorf("%s = %q, want the installed table %q", e.savedFiltered(), got, tc.want)
	}

	for _, saved := range []string{e.savedBefore(), e.savedFiltered()} {
		if info, err := os.Stat(saved); err == nil && info.Mode().Perm()&0o077 != 0 {
			t.Errorf("%s mode = %o, want no access for group and others", saved, info.Mode().Perm())
		}
	}
}

func TestRetireGuide_RetirementRunTwiceKeepsTheUndo_AC3(t *testing.T) {
	t.Parallel()

	line := onlyLine(t, "grep -vxF")
	original := "MAILTO=root\n*/10 * * * * /usr/local/psa/admin/bin/psaadm-job\n" + scriptCronLine + "\n"

	t.Run("AC3 a rerun after the install", func(t *testing.T) {
		e := newRetireEnv(t)
		e.fill()
		e.setCrontab(original)
		e.run(e.substitute(line), true, e.home)

		before, filtered := e.read(e.savedBefore()), e.read(e.savedFiltered())
		if before != original || filtered == "(absent)" {
			t.Fatalf("after the first run %s = %q and %s = %q, want the crontab of before and the filtered one", e.savedBefore(), before, e.savedFiltered(), filtered)
		}

		e.run(e.substitute(line), true, e.home)

		if got := e.read(e.savedBefore()); got != before {
			t.Errorf("%s = %q after the rerun, want it unchanged %q", e.savedBefore(), got, before)
		}

		if got := e.read(e.savedFiltered()); got != filtered {
			t.Errorf("%s = %q after the rerun, want it unchanged %q", e.savedFiltered(), got, filtered)
		}

		if n := e.installs(); n != 1 {
			t.Errorf("crontab installed %d times in two runs, want 1", n)
		}
	})

	t.Run("AC3 a rerun while the crontab still holds the line", func(t *testing.T) {
		e := newRetireEnv(t)
		e.fill()
		e.setCrontab(original)
		e.run(e.substitute(line), true, e.home)

		before, filtered := e.read(e.savedBefore()), e.read(e.savedFiltered())

		e.setCrontab(original + "5 5 * * * /changed\n")
		e.run(e.substitute(line), true, e.home)

		if got := e.read(e.savedBefore()); got != before {
			t.Errorf("%s = %q after the rerun, want it unchanged %q", e.savedBefore(), got, before)
		}

		if got := e.read(e.savedFiltered()); got != filtered {
			t.Errorf("%s = %q after the rerun, want it unchanged %q", e.savedFiltered(), got, filtered)
		}

		if n := e.installs(); n != 1 {
			t.Errorf("crontab installed %d times in two runs, want 1", n)
		}
	})
}

func TestRetireGuide_UndoRestoresTheLine_AC3(t *testing.T) {
	t.Parallel()

	retire := onlyLine(t, "grep -vxF")
	undo := onlyLine(t, "cmp -s")
	original := "MAILTO=root\n*/10 * * * * /usr/local/psa/admin/bin/psaadm-job\n" + scriptCronLine + "\n"

	t.Run("AC3 the crontab is as the retirement left it", func(t *testing.T) {
		e := newRetireEnv(t)
		e.fill()
		e.setCrontab(original)
		e.run(e.substitute(retire), true, e.home)

		e.run(e.substitute(undo), true, e.home)

		if got := e.read(e.state); got != original {
			t.Errorf("crontab after the undo = %q, want the crontab of before %q", got, original)
		}

		for _, saved := range []string{e.savedBefore(), e.savedFiltered()} {
			if got := e.read(saved); got != "(absent)" {
				t.Errorf("%s = %q after the undo, want it removed", saved, got)
			}
		}
	})

	t.Run("AC3 the crontab changed since", func(t *testing.T) {
		e := newRetireEnv(t)
		e.fill()
		e.setCrontab(original)
		e.run(e.substitute(retire), true, e.home)

		changed := e.read(e.state) + "5 5 * * * /changed\n"
		e.setCrontab(changed)
		installs := e.installs()

		e.run(e.substitute(undo), true, e.home)

		if n := e.installs(); n != installs {
			t.Errorf("crontab installed %d times, want %d: the undo installs nothing over a changed crontab", n, installs)
		}

		if got := e.read(e.state); got != changed {
			t.Errorf("crontab after the undo = %q, want it untouched %q", got, changed)
		}
	})
}

func TestRetireGuide_StepsInOrder_AC3(t *testing.T) {
	t.Parallel()

	lines := retireGuideLines(t)

	tars := indexesOf(lines, "tar -czf -")
	imports := indexesOf(lines, "vandoxd.dll import")
	copies := indexesOf(lines, "scp ")
	retire := indexesOf(lines, "grep -vxF")
	waits := indexesOf(lines, "stat ")
	undo := indexesOf(lines, "cmp -s")

	if len(tars) != 2 || len(imports) < 2 || len(copies) < 2 || len(retire) != 1 || len(waits) == 0 || len(undo) != 1 {
		t.Fatalf("lines: %d pack, %d import, %d scp, %d retirement, %d stat, %d undo, want 2, at least 2, at least 2, 1, at least 1, 1",
			len(tars), len(imports), len(copies), len(retire), len(waits), len(undo))
	}

	firstCopy := firstAfter(copies, tars[0])
	firstImport := firstAfter(imports, firstCopy)
	wait := firstAfter(waits, retire[0])
	secondCopy := firstAfter(copies, tars[1])
	secondImport := firstAfter(imports, secondCopy)

	order := []struct {
		what  string
		index int
	}{
		{"pack of step 1", tars[0]}, {"copy of step 1", firstCopy}, {"import of step 1", firstImport}, {"retirement", retire[0]},
		{"wait for the log to stop growing", wait}, {"pack of step 3", tars[1]}, {"copy of step 3", secondCopy},
		{"import of step 3", secondImport}, {"undo", undo[0]},
	}

	previous := -1
	for _, step := range order {
		if step.index < 0 || step.index <= previous {
			t.Errorf("AC3 %s is at line %d after line %d, want pack, copy, import, retirement, wait, pack, copy, import, undo in that order", step.what, step.index, previous)
		}

		previous = step.index
	}

	assertDeleteAfterCopy(t, lines, tars, copies)
}

// assertDeleteAfterCopy checks that the pack block holds no delete and that a separate block after each copy deletes
// the archive of its pack line.
func assertDeleteAfterCopy(t *testing.T, lines []guideLine, tars, copies []int) {
	t.Helper()

	deletes := indexesOf(lines, "rm ")
	scripts := packScripts(t)

	for k, i := range tars {
		name := archiveName(t, scripts[k])
		copied := firstAfter(copies, i)
		deleted := false

		for _, j := range deletes {
			if lines[j].block == lines[i].block {
				t.Errorf("AC3 the pack line's block holds %q, want the delete in a separate block after the copy", lines[j].text)
			}

			if copied >= 0 && j > copied && strings.Contains(lines[j].text, name) && lines[j].block != lines[copied].block {
				deleted = true
			}
		}

		if deleted == false {
			t.Errorf("AC3 no delete block of %q follows the copy after pack line %d (line %d), want one", name, k+1, i)
		}
	}
}

// firstAfter returns the first index of indexes that is greater than after, or -1.
func firstAfter(indexes []int, after int) int {
	for _, index := range indexes {
		if index > after {
			return index
		}
	}

	return -1
}
