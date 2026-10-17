package procstats

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// nameCase is a case of the shared fixture testdata/processes/names.json, which the C# tests read too.
type nameCase struct {
	Name     string `json:"name"`
	Cmdline  string `json:"cmdline"`
	Fallback string `json:"fallback"`
	Command  string `json:"command"`
	Program  string `json:"program"`
}

// sharedNameCases reads the shared cases.
func sharedNameCases(t *testing.T) []nameCase {
	t.Helper()

	data, err := os.ReadFile(namesFile)
	if err != nil {
		t.Fatalf("reading %s: %v", namesFile, err)
	}
	var doc struct {
		Cases []nameCase `json:"cases"`
	}
	if err := json.Unmarshal(data, &doc); err != nil {
		t.Fatalf("decoding %s: %v", namesFile, err)
	}
	if len(doc.Cases) < 13 {
		t.Fatalf("%s holds %d cases, want at least the 13 of the plan", namesFile, len(doc.Cases))
	}
	return doc.Cases
}

const namesFile = "../../testdata/processes/names.json"

func TestCommandOf_SharedCases(t *testing.T) {
	for _, tt := range sharedNameCases(t) {
		t.Run(tt.Name, func(t *testing.T) {
			if got := commandOf(tt.Cmdline, tt.Fallback); got != tt.Command {
				t.Errorf("commandOf(%q, %q) = %q, want %q", tt.Cmdline, tt.Fallback, got, tt.Command)
			}
		})
	}
}

func TestProgramOf_SharedCases(t *testing.T) {
	for _, tt := range sharedNameCases(t) {
		t.Run(tt.Name, func(t *testing.T) {
			if got := programOf(tt.Command); got != tt.Program {
				t.Errorf("programOf(%q) = %q, want %q", tt.Command, got, tt.Program)
			}
		})
	}
}

func TestCommandFor_EmptyCommUsesUnnamed(t *testing.T) {
	tests := []struct {
		name, cmdline, comm, want string
	}{
		{"empty command line and empty comm", "", "", UnnamedCommand},
		{"empty command line and a comm", "", "x", "x"},
		{"long comm of a kernel thread", "", "kworker/u16:0-kvfree_rcu_reclaim", "kworker/u16:0-kvfree_rcu_reclaim"},
		{"command line wins over comm", "/usr/bin/x -a", "y", "x"},
		{"command line and empty comm", "/usr/bin/x -a", "", "x"},
		{"command line of spaces and empty comm", "  ", "", "  "},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := commandFor(tt.cmdline, tt.comm); got != tt.want {
				t.Errorf("commandFor(%q, %q) = %q, want %q", tt.cmdline, tt.comm, got, tt.want)
			}
		})
	}
}

func TestProgramFor_Rules(t *testing.T) {
	fpm := Rule{Program: "php-fpm", CmdlinePrefix: "php-fpm: pool ", AppendNextWord: true}
	plain := Rule{Program: "pool", CmdlinePrefix: "php-fpm: pool "}
	web := Rule{Program: "web", Command: "apache2"}
	tests := []struct {
		name    string
		rules   []Rule
		cmdline string
		command string
		want    string
	}{
		{"command rule", []Rule{web}, "/usr/sbin/apache2 -k start", "apache2", "web"},
		{"command rule needs the whole command", []Rule{web}, "/usr/sbin/apache2ctl", "apache2ctl", "apache2ctl"},
		{"command rule is case-sensitive", []Rule{web}, "Apache2", "Apache2", "Apache2"},
		{"prefix rule", []Rule{plain}, "php-fpm: pool example.com", "php-fpm", "pool"},
		{"prefix rule is case-sensitive", []Rule{plain}, "PHP-FPM: pool example.com", "PHP-FPM", "PHP-FPM"},
		{"appended word", []Rule{fpm}, "php-fpm: pool example.com", "php-fpm", "php-fpm/example.com"},
		{"word ends at the next space", []Rule{fpm}, "php-fpm: pool a.com extra words", "php-fpm", "php-fpm/a.com"},
		{"empty word", []Rule{fpm}, "php-fpm: pool ", "php-fpm", "php-fpm"},
		{"empty word before a space", []Rule{fpm}, "php-fpm: pool  x", "php-fpm", "php-fpm"},
		{"prefix equal to the whole command line", []Rule{{Program: "p", CmdlinePrefix: "abc", AppendNextWord: true}}, "abc", "abc", "p"},
		{"prefix equal to the whole command line without the appended word", []Rule{{Program: "p", CmdlinePrefix: "abc"}}, "abc", "abc", "p"},
		{"prefix longer than the command line", []Rule{{Program: "p", CmdlinePrefix: "abcd"}}, "abc", "abc", "abc"},
		{"first rule wins", []Rule{{Program: "one", Command: "x"}, {Program: "two", Command: "x"}}, "x", "x", "one"},
		{"first matching rule wins over an earlier miss", []Rule{{Program: "one", Command: "y"}, {Program: "two", Command: "x"}}, "x", "x", "two"},
		{"no match takes the default", []Rule{web, fpm}, "spamd child", "spamd", "spamd"},
		{"no rules take the default", nil, "spamd child", "spamd", "spamd"},
		{"default is the first word of the command", []Rule{web}, "[a b]", "a b", "a"},
		{"word cut at a character boundary", []Rule{{Program: "pp", CmdlinePrefix: "x ", AppendNextWord: true}}, "x " + strings.Repeat("\u00e9", 600), "x",
			"pp/" + strings.Repeat("\u00e9", 510)},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := programFor(tt.rules, tt.cmdline, tt.command)

			if got != tt.want {
				t.Errorf("programFor(%+v, %q, %q) = %q, want %q", tt.rules, tt.cmdline, tt.command, got, tt.want)
			}
		})
	}
}

func TestProgramFor_AppendedProgramPassesModel(t *testing.T) {
	rule := Rule{Program: strings.Repeat("a", 128), CmdlinePrefix: "x ", AppendNextWord: true}
	cmdline := "x " + strings.Repeat("\u00e9", 600)

	got := programFor([]Rule{rule}, cmdline, "x")

	if len(got) > 1024 || !utf8.ValidString(got) || !strings.HasPrefix(got, rule.Program+"/") {
		t.Fatalf("programFor() = %d bytes (valid UTF-8 %v), want at most 1024 bytes of valid UTF-8 starting with the rule's program and a slash", len(got), utf8.ValidString(got))
	}
	size, err := checkProgramEntry(&model.ProgramAggregate{Program: got, Count: 1})
	if err != nil || size <= len(got) {
		t.Errorf("checkProgramEntry(appended program) = %d, %v, want its encoded size and no error", size, err)
	}
}
