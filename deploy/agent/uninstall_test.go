package agent

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const (
	uninstallScript = "uninstall.sh"

	// uninstallUsageLine is the usage text of uninstall.sh (plan.md, Approach, Output).
	uninstallUsageLine = "usage: uninstall.sh [--purge]"

	// removeSQLFileName is the shipped script that drops the MariaDB account of the agent.
	removeSQLFileName = "mariadb-user-remove.sql"

	// removeStatement is the one statement the removal script holds (plan.md, Approach, Files).
	removeStatement = "DROP USER IF EXISTS 'vandox-agent'@'localhost';"
)

// runUninstall sources uninstall.sh after the install scripts (its functions replace the ones of the same
// name) and runs script with args as $1....
func (h *harness) runUninstall(script string, args ...string) shellResult {
	h.t.Helper()

	return h.run("source "+shellQuote(shippedFile(h.t, uninstallScript))+"; "+script, "", args...)
}

// TestUninstall_UsageErrorsExitTwo covers AC7: every argument list other than none or --purge is refused
// with the usage line on standard error and exit status 2, and the two valid forms set PURGE differently.
func TestUninstall_UsageErrorsExitTwo(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name string
		args []string
	}{
		{"unknown option", []string{"--force"}},
		{"purge with another option", []string{"--purge", "--force"}},
		{"another option before purge", []string{"--force", "--purge"}},
		{"purge without dashes", []string{"purge"}},
		{"single dash purge", []string{"-purge"}},
		{"short option", []string{"-p"}},
		{"upper case purge", []string{"--PURGE"}},
		{"purge with a value", []string{"--purge=1"}},
		{"purge with a suffix", []string{"--purge-all"}},
		{"help option", []string{"--help"}},
		{"positional argument", []string{"web-1"}},
		{"empty argument", []string{""}},
		{"purge followed by an empty argument", []string{"--purge", ""}},
		{"install option", []string{"--version", "v1.2.3"}},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.runUninstall(`parse_args "$@"`, tc.args...)

			expectResult(t, "parse_args "+strings.Join(tc.args, " "), got, 2, "", uninstallUsageLine+"\n")
		})
	}

	t.Run("no arguments and --purge set PURGE", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)

		plain := h.runUninstall(`parse_args; printf '%s' "$PURGE"`)
		purge := h.runUninstall(`parse_args "$@"; printf '%s' "$PURGE"`, "--purge")

		if plain.code != 0 || plain.stderr != "" {
			t.Errorf("parse_args without arguments: exit status %d, standard error %q, want 0 and empty", plain.code, plain.stderr)
		}

		if purge.code != 0 || purge.stderr != "" {
			t.Errorf("parse_args --purge: exit status %d, standard error %q, want 0 and empty", purge.code, purge.stderr)
		}

		if plain.stdout == "" || purge.stdout == "" || plain.stdout == purge.stdout {
			t.Errorf("PURGE without arguments = %q and with --purge = %q, want two different non-empty values", plain.stdout, purge.stdout)
		}
	})
}

// TestMariadbRemoveSQL_DropsOnlyTheAgentAccount checks that the shipped removal script holds, besides
// comments and blank lines, exactly the one DROP USER statement for the agent's account and nothing else.
func TestMariadbRemoveSQL_DropsOnlyTheAgentAccount(t *testing.T) {
	t.Parallel()

	data, err := os.ReadFile(shippedFile(t, removeSQLFileName))
	if err != nil {
		t.Fatalf("read %s: %v", removeSQLFileName, err)
	}

	var statements []string

	for _, line := range strings.Split(string(data), "\n") {
		trimmed := strings.TrimSpace(line)
		if trimmed == "" || strings.HasPrefix(trimmed, "--") {
			continue
		}

		statements = append(statements, trimmed)
	}

	if len(statements) != 1 || statements[0] != removeStatement {
		t.Errorf("statements of %s = %q, want exactly [%q]", removeSQLFileName, statements, removeStatement)
	}
}

// TestAgentYAML_HoldsTheLinesRenderConfigReplaces pins the example file to the two whole lines render_config
// replaces (lines 20 and 27) and to the spool block the spool reader and the uninstall script read
// (lines 34 to 37), each exactly once.
func TestAgentYAML_HoldsTheLinesRenderConfigReplaces(t *testing.T) {
	t.Parallel()

	data, err := os.ReadFile(shippedFile(t, "agent.yaml"))
	if err != nil {
		t.Fatalf("read agent.yaml: %v", err)
	}

	lines := strings.Split(string(data), "\n")

	line := func(n int) string {
		if n > len(lines) {
			return ""
		}

		return lines[n-1]
	}

	if got := line(20); got != "agent_id: web-1" {
		t.Errorf("agent.yaml line 20 = %q, want %q", got, "agent_id: web-1")
	}

	if got := line(27); got != "  url: http://100.64.0.1:8081" {
		t.Errorf("agent.yaml line 27 = %q, want %q", got, "  url: http://100.64.0.1:8081")
	}

	if got := line(34); got != "spool:" {
		t.Errorf("agent.yaml line 34 = %q, want %q", got, "spool:")
	}

	if got := line(37); got != "  directory: "+defaultSpool {
		t.Errorf("agent.yaml line 37 = %q, want %q", got, "  directory: "+defaultSpool)
	}

	for _, want := range []string{"agent_id: web-1", "  url: http://100.64.0.1:8081", "spool:"} {
		count := 0

		for _, l := range lines {
			if l == want {
				count++
			}
		}

		if count != 1 {
			t.Errorf("agent.yaml holds the line %q %d times, want once", want, count)
		}
	}
}

// dropAccountCase is one setup of _drop_account: which account tools exist and whether they succeed.
type dropAccountCase struct {
	name       string
	plesk      string // "", "ok" or "fail"
	mariadb    string // "", "ok" or "fail"
	missingSQL bool
	want       string // the one line printed on standard output
	wantCalled string // the tool that must have run: "plesk", "mariadb" or ""
}

// accountToolBody is the body of a stub account tool that reads its standard input into a file.
func accountToolBody(name, mode string) string {
	if mode == "fail" {
		return `cat >/dev/null; echo "ERROR 1045 (28000): Access denied" >&2; exit 1`
	}

	return `cat >"$STUB_LOG/` + name + `.stdin"`
}

// TestDropAccount_RunsPleskOrMariadbAndReportsTheOutcome covers the removal of the MariaDB account: plesk db
// is used when plesk is on the search path (and mariadb then never runs), the SQL file reaches the tool on
// standard input, and the exact line says removed or names the command to run.
func TestDropAccount_RunsPleskOrMariadbAndReportsTheOutcome(t *testing.T) {
	t.Parallel()

	const (
		removed  = "uninstall.sh: removed MariaDB account\n"
		warnTpl  = "uninstall.sh: WARNING MariaDB account not removed; run: %s < {SQL}\n"
		sqlBytes = "DROP USER IF EXISTS 'x'@'localhost';\n"
	)

	warn := func(tool string) string { return strings.Replace(warnTpl, "%s", tool, 1) }

	cases := []dropAccountCase{
		{name: "plesk succeeds", plesk: "ok", mariadb: "ok", want: removed, wantCalled: "plesk"},
		{name: "plesk succeeds without mariadb", plesk: "ok", want: removed, wantCalled: "plesk"},
		{name: "plesk fails and mariadb is not tried", plesk: "fail", mariadb: "ok", want: warn("plesk db"), wantCalled: "plesk"},
		{name: "mariadb only succeeds", mariadb: "ok", want: removed, wantCalled: "mariadb"},
		{name: "mariadb only fails", mariadb: "fail", want: warn("mariadb"), wantCalled: "mariadb"},
		{name: "neither tool exists", want: warn("mariadb")},
		{name: "plesk and a missing SQL file", plesk: "ok", missingSQL: true, want: warn("plesk db")},
		{name: "mariadb and a missing SQL file", mariadb: "ok", missingSQL: true, want: warn("mariadb")},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			runDropAccountCase(t, tc, sqlBytes)
		})
	}
}

// runDropAccountCase runs _drop_account for one case and checks the line, the calls and the standard input.
func runDropAccountCase(t *testing.T, tc dropAccountCase, sqlBytes string) {
	t.Helper()

	h := newHarness(t)

	for tool, mode := range map[string]string{"plesk": tc.plesk, "mariadb": tc.mariadb} {
		if mode != "" {
			h.stub(tool, accountToolBody(tool, mode))
		}
	}

	sqlPath := filepath.Join(h.dir, "missing.sql")
	if tc.missingSQL == false {
		sqlPath = h.writeFile("remove.sql", sqlBytes)
	}

	got := h.runUninstall(`_drop_account "$1"`, sqlPath)

	// The warning names the SQL file that was given, not a path of the repository.
	want := strings.ReplaceAll(tc.want, "{SQL}", sqlPath)

	expectResult(t, "_drop_account", got, 0, want, "")

	for _, tool := range []string{"plesk", "mariadb"} {
		assertToolCalls(t, h, tool, tc.wantCalled)
	}

	if tc.want == "uninstall.sh: removed MariaDB account\n" {
		stdin, err := os.ReadFile(filepath.Join(h.logs, tc.wantCalled+".stdin"))
		if err != nil || string(stdin) != sqlBytes {
			t.Errorf("standard input of %s = %q (%v), want the SQL file %q", tc.wantCalled, stdin, err, sqlBytes)
		}
	}
}

// assertToolCalls checks that tool ran exactly once with its usual arguments when it is the expected one,
// and not at all otherwise.
func assertToolCalls(t *testing.T, h *harness, tool, expected string) {
	t.Helper()

	calls := h.calls(tool)
	if tool != expected {
		assertCalls(t, tool, calls, nil)

		return
	}

	wantArgs := "[db]"
	if tool == "mariadb" {
		wantArgs = ""
	}

	assertCalls(t, tool, calls, []string{wantArgs})
}

// purgeUserCase is one account state for _purge_user.
type purgeUserCase struct {
	name     string
	passwd   string // the getent passwd line, empty for no user
	group    bool   // the group still exists after the user is deleted
	want     string
	wantUser bool // userdel must run
}

// TestPurgeUser_DeletesTheUserNamesAHomeAndRemovesALeftoverGroup covers the user part of the purge: userdel
// never gets -r, a home directory other than /nonexistent is named and left, and a group that remains is
// removed.
func TestPurgeUser_DeletesTheUserNamesAHomeAndRemovesALeftoverGroup(t *testing.T) {
	t.Parallel()

	entry := func(home string) string {
		return "vandox-agent:x:998:998:vandox-agent:" + home + ":/usr/sbin/nologin"
	}

	cases := []purgeUserCase{
		{
			name: "user with a home directory and a leftover group", passwd: entry("/home/vandox-agent"), group: true, wantUser: true,
			want: "uninstall.sh: removed user vandox-agent\nuninstall.sh: home directory /home/vandox-agent left in place\nuninstall.sh: removed group vandox-agent\n",
		},
		{name: "user with the nonexistent home", passwd: entry("/nonexistent"), wantUser: true, want: "uninstall.sh: removed user vandox-agent\n"},
		{name: "user with an empty home field", passwd: entry(""), wantUser: true, want: "uninstall.sh: removed user vandox-agent\n"},
		{
			name: "user with the nonexistent home and a leftover group", passwd: entry("/nonexistent"), group: true, wantUser: true,
			want: "uninstall.sh: removed user vandox-agent\nuninstall.sh: removed group vandox-agent\n",
		},
		{name: "user absent, group left", group: true, want: "uninstall.sh: user vandox-agent not present\nuninstall.sh: removed group vandox-agent\n"},
		{name: "user and group absent", want: "uninstall.sh: user vandox-agent not present\n"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			runPurgeUserCase(t, tc)
		})
	}
}

// runPurgeUserCase runs _purge_user with stub getent, userdel and groupdel for one case.
func runPurgeUserCase(t *testing.T, tc purgeUserCase) {
	t.Helper()

	h := newHarness(t)

	h.stub("getent", `case $1 in
passwd) if [[ -n ${STUB_PASSWD:-} ]]; then echo "$STUB_PASSWD"; else exit 2; fi ;;
group) if [[ -n ${STUB_GROUP:-} ]]; then echo "$STUB_GROUP"; else exit 2; fi ;;
*) exit 2 ;;
esac`)
	h.stub("userdel", "exit 0")
	h.stub("groupdel", "exit 0")

	h.setEnv("STUB_PASSWD=" + tc.passwd)

	if tc.group {
		h.setEnv("STUB_GROUP=vandox-agent:x:998:")
	}

	got := h.runUninstall(`_purge_user`)

	expectResult(t, "_purge_user", got, 0, tc.want, "")

	wantUserdel := []string(nil)
	if tc.wantUser {
		wantUserdel = []string{"[vandox-agent]"}
	}

	assertCalls(t, "userdel", h.calls("userdel"), wantUserdel)

	wantGroupdel := []string(nil)
	if tc.group {
		wantGroupdel = []string{"[vandox-agent]"}
	}

	assertCalls(t, "groupdel", h.calls("groupdel"), wantGroupdel)
}

// assertCalls compares the recorded argument lines of a stub with the expected ones.
func assertCalls(t *testing.T, name string, got, want []string) {
	t.Helper()

	if strings.Join(got, "\n") != strings.Join(want, "\n") {
		t.Errorf("%s calls = %q, want %q", name, got, want)
	}
}

// TestRemoveFile_RemovesAFileOrALinkNeverItsTarget covers the three answers of _remove_file.
func TestRemoveFile_RemovesAFileOrALinkNeverItsTarget(t *testing.T) {
	t.Parallel()

	t.Run("a file is removed", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		path := h.writeFile("etc/unit", "x")

		got := h.runUninstall(`_remove_file "$1" unit`, path)

		expectResult(t, "_remove_file", got, 0, "uninstall.sh: removed unit\n", "")

		if _, err := os.Lstat(path); err == nil {
			t.Errorf("%s still exists", path)
		}
	})

	t.Run("a missing path is reported", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)

		got := h.runUninstall(`_remove_file "$1" binary`, filepath.Join(h.dir, "absent"))

		expectResult(t, "_remove_file", got, 0, "uninstall.sh: binary not present\n", "")
	})

	t.Run("a link is removed and its target kept", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		target := h.writeFile("target", "precious")
		link := filepath.Join(h.dir, "link")

		if err := os.Symlink(target, link); err != nil {
			t.Fatalf("symlink: %v", err)
		}

		got := h.runUninstall(`_remove_file "$1" token`, link)

		expectResult(t, "_remove_file", got, 0, "uninstall.sh: removed token\n", "")

		if _, err := os.Lstat(link); err == nil {
			t.Errorf("the link %s still exists", link)
		}

		if data, err := os.ReadFile(target); err != nil || string(data) != "precious" {
			t.Errorf("the target = %q (%v), want it untouched", data, err)
		}
	})

	t.Run("a dangling link is removed", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		link := filepath.Join(h.dir, "dangling")

		if err := os.Symlink(filepath.Join(h.dir, "nowhere"), link); err != nil {
			t.Fatalf("symlink: %v", err)
		}

		got := h.runUninstall(`_remove_file "$1" token`, link)

		expectResult(t, "_remove_file", got, 0, "uninstall.sh: removed token\n", "")
	})
}

const (
	entryStart = "# --- entry check (before anything is sourced) ---"
	entryEnd   = "# --- end of entry check ---"
	entryText  = "is not owned by root or is writable by others; extract the bundle as root in a private directory"
)

// entryBlock returns the text between the two entry-check markers of a script, with the script's name
// (and the variable names derived from it) normalized.
func entryBlock(t *testing.T, script string) string {
	t.Helper()

	data, err := os.ReadFile(shippedFile(t, script))
	if err != nil {
		t.Fatalf("read %s: %v", script, err)
	}

	text := string(data)

	for _, marker := range []string{entryStart, entryEnd} {
		if strings.Count(text, marker) != 1 {
			t.Fatalf("%s holds the marker %q %d times, want once", script, marker, strings.Count(text, marker))
		}
	}

	block := text[strings.Index(text, entryStart)+len(entryStart) : strings.Index(text, entryEnd)]

	return strings.ReplaceAll(block, "uninstall", "install")
}

// TestEntryCheck_IsTheSameInBothScripts checks that install.sh and uninstall.sh carry the same inline entry
// check, apart from the script name, and that it is the real check and not a placeholder.
func TestEntryCheck_IsTheSameInBothScripts(t *testing.T) {
	t.Parallel()

	install := entryBlock(t, installScript)
	uninstall := entryBlock(t, uninstallScript)

	if install != uninstall {
		t.Errorf("the entry check differs between the scripts:\ninstall.sh:%s\nuninstall.sh:%s", install, uninstall)
	}

	for name, block := range map[string]string{installScript: install, uninstallScript: uninstall} {
		if strings.Contains(block, entryText) == false {
			t.Errorf("the entry check of %s lacks the refusal text %q", name, entryText)
		}
	}
}
