package agent

import (
	"os"
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
