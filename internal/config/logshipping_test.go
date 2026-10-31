package config

import (
	"slices"
	"strconv"
	"strings"
	"testing"
)

// The reasons log_shipping.drop is refused with, fixed by the plan.
const (
	patternReason  = "must be an RE2 pattern of at most 1024 bytes and 400 compiled instructions"
	togetherReason = "patterns must compile to at most 400 instructions together"
	formatReason   = "must be one of syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error"
)

// shippingDoc returns a valid agent file with the given lines below log_shipping.
func shippingDoc(lines ...string) string {
	return validAgent + "log_shipping:\n" + strings.Join(lines, "\n") + "\n"
}

// markedLine returns the 1-based line of doc that holds the marker "# bad".
func markedLine(t *testing.T, doc string) int {
	t.Helper()
	i := strings.Index(doc, "# bad")
	if i < 0 {
		t.Fatalf("the document has no line marked with %q: %q", "# bad", doc)
	}
	return strings.Count(doc[:i], "\n") + 1
}

// ruleLine returns a drop rule with a message in a flow mapping on one line.
func ruleLine(pattern string) string { return "    - {message: '" + pattern + "'}" }

func TestPriorityLevel_NamesTheSyslogPriorities(t *testing.T) {
	names := []string{"emerg", "alert", "crit", "err", "warning", "notice", "info", "debug"}
	for want, name := range names {
		if got, ok := PriorityLevel(name); !ok || int(got) != want {
			t.Errorf("PriorityLevel(%q) = %d, %v, want %d, true", name, got, ok, want)
		}
	}
	for _, name := range []string{"", "warn", "error", "INFO", "Info", "7", "info ", "panic"} {
		if got, ok := PriorityLevel(name); ok {
			t.Errorf("PriorityLevel(%q) = %d, true, want false", name, got)
		}
	}
}

func TestLoadAgent_LogShippingDefaults(t *testing.T) {
	docs := map[string]string{
		"section omitted": validAgent,
		"empty section":   validAgent + "log_shipping:\n",
		"empty lists":     shippingDoc("  files: []", "  drop: []"),
	}
	for name, doc := range docs {
		t.Run(name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", doc, err)
			}
			ls := got.LogShipping
			if !ls.Journal || ls.MinPriority != "info" || len(ls.Files) != 0 || len(ls.Drop) != 0 {
				t.Errorf("LoadAgent(%q) LogShipping = %+v, want journal true, min_priority info and no files and no drop rules", doc, ls)
			}
		})
	}
	t.Run("DefaultAgent", func(t *testing.T) {
		ls := DefaultAgent().LogShipping
		if !ls.Journal || ls.MinPriority != "info" || len(ls.Files) != 0 || len(ls.Drop) != 0 {
			t.Errorf("DefaultAgent().LogShipping = %+v, want journal true, min_priority info and no files and no drop rules", ls)
		}
	})
}

// fileLines returns the lines of n files /var/log/f0.log ... below log_shipping.files and the files.
func fileLines(n int) ([]string, []LogFile) {
	lines := []string{"  files:"}
	var files []LogFile
	for i := 0; i < n; i++ {
		path := "/var/log/f" + strconv.Itoa(i) + ".log"
		lines = append(lines, "    - {path: "+path+", format: syslog}")
		files = append(files, LogFile{Path: path, Format: "syslog"})
	}
	return lines, files
}

// ruleLines returns the lines of n drop rules of programs p0 ... and the rules.
func ruleLines(n int) ([]string, []DropRule) {
	lines := []string{"  drop:"}
	var rules []DropRule
	for i := 0; i < n; i++ {
		program := "p" + strconv.Itoa(i)
		lines = append(lines, "    - {program: "+program+"}")
		rules = append(rules, DropRule{Program: program})
	}
	return lines, rules
}

// acceptedCase is a log_shipping section the loader takes, and the options it must make of it.
type acceptedCase struct {
	name  string
	lines []string
	want  LogShipping
}

func shipping(journal bool, minPriority string, files []LogFile, drop []DropRule) LogShipping {
	return LogShipping{Journal: journal, MinPriority: minPriority, Files: files, Drop: drop}
}

func acceptedCases() []acceptedCase {
	files16Lines, files16 := fileLines(MaxLogFiles)
	rules64Lines, rules64 := ruleLines(MaxDropRules)
	costFreeLines, costFree := ruleLines(19)
	long := strings.Repeat("(?i)", 255) + "abcd" // 1,024 bytes, a handful of instructions
	one := func(rule DropRule) []DropRule { return []DropRule{rule} }
	message := func(pattern string) []string { return []string{"  drop:", ruleLine(pattern)} }
	mail := LogFile{Path: "/var/log/mail.log", Format: "syslog"}
	full := DropRule{Log: "journal", Program: "dovecot", Priority: "info", Message: "^imap-login: Login: "}
	return []acceptedCase{
		{"journal off", []string{"  journal: false"}, shipping(false, "info", nil, nil)},
		{"a minimum of err", []string{"  min_priority: err"}, shipping(true, "err", nil, nil)},
		{"a file", []string{"  files:", "    - {path: /var/log/mail.log, format: syslog}"}, shipping(true, "info", []LogFile{mail}, nil)},
		{"16 files", files16Lines, shipping(true, "info", files16, nil)},
		{"a plesk-panel file", []string{"  files:", "    - {path: /var/log/plesk/panel.log, format: plesk-panel}"}, shipping(true, "info", []LogFile{{Path: "/var/log/plesk/panel.log", Format: "plesk-panel"}}, nil)},
		{"a sw-cp-server-error file", []string{"  files:", "    - {path: /var/log/sw-cp-server/error_log, format: sw-cp-server-error}"}, shipping(true, "info", []LogFile{{Path: "/var/log/sw-cp-server/error_log", Format: "sw-cp-server-error"}}, nil)},
		{"an nginx-error file", []string{"  files:", "    - {path: /var/log/nginx/error.log, format: nginx-error}"}, shipping(true, "info", []LogFile{{Path: "/var/log/nginx/error.log", Format: "nginx-error"}}, nil)},
		{"an apache-error file", []string{"  files:", "    - {path: /var/log/apache2/error.log, format: apache-error}"}, shipping(true, "info", []LogFile{{Path: "/var/log/apache2/error.log", Format: "apache-error"}}, nil)},
		{"a rule with every condition", []string{"  drop:", "    - {log: journal, program: dovecot, priority: info, message: '^imap-login: Login: '}"}, shipping(true, "info", nil, one(full))},
		{"a rule for a configured file", []string{"  files:", "    - {path: /var/log/mail.log, format: syslog}", "  drop:", "    - {log: /var/log/mail.log, priority: debug}"},
			shipping(true, "info", []LogFile{mail}, one(DropRule{Log: "/var/log/mail.log", Priority: "debug"}))},
		{"64 rules", rules64Lines, shipping(true, "info", nil, rules64)},
		{"a pattern of 1,024 bytes", message(long), shipping(true, "info", nil, one(DropRule{Message: long}))},
		{"a pattern of 399 instructions", message(`\pL{0,198}y`), shipping(true, "info", nil, one(DropRule{Message: `\pL{0,198}y`}))},
		{"a case-insensitive pattern of 399 instructions", message(`(?i)\pL{0,198}y`), shipping(true, "info", nil, one(DropRule{Message: `(?i)\pL{0,198}y`}))},
		{"a pattern of 400 instructions", message(`[a-z]{397}y`), shipping(true, "info", nil, one(DropRule{Message: `[a-z]{397}y`}))},
		{"two patterns of 201 and 199 instructions together", append(message(`\pL{0,99}y`), ruleLine(`\pL{0,98}y`)),
			shipping(true, "info", nil, []DropRule{{Message: `\pL{0,99}y`}, {Message: `\pL{0,98}y`}})},
		{"rules without a message cost no instruction", append(message(`[a-z]{397}y`), costFreeLines[1:]...),
			shipping(true, "info", nil, append(one(DropRule{Message: `[a-z]{397}y`}), costFree...))},
	}
}

func TestLoadAgent_LogShippingAccepted(t *testing.T) {
	for _, tt := range acceptedCases() {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, shippingDoc(tt.lines...)), agentEnv())

			if err != nil {
				t.Fatalf("LoadAgent() error = %v, want nil", err)
			}
			if !sameShipping(got.LogShipping, tt.want) {
				t.Errorf("LoadAgent() LogShipping = %.300v, want %.300v", got.LogShipping, tt.want)
			}
		})
	}
}

// sameShipping reports whether two sets of options hold the same values; an empty list equals a missing one.
func sameShipping(a, b LogShipping) bool {
	return a.Journal == b.Journal && a.MinPriority == b.MinPriority && slices.Equal(a.Files, b.Files) && slices.Equal(a.Drop, b.Drop)
}

// shippingError is a log_shipping section the loader refuses, and how.
type shippingError struct {
	name  string
	lines []string
	key   string // the key the error names
	why   string // the exact reason, or "" for any
}

func shippingErrors() []shippingError {
	overlong := strings.Repeat("(?i)", 260) + "a" // 1,041 bytes, few instructions
	rules65, _ := ruleLines(MaxDropRules + 1)
	rules65[0] = "  drop: # bad"
	files17, _ := fileLines(MaxLogFiles + 1)
	files17[0] = "  files: # bad"
	file := func(path, format string) string { return "    - {path: " + path + ", format: " + format + "} # bad" }
	bad := func(pattern string) []string { return []string{"  drop:", ruleLine(pattern) + " # bad"} }
	return []shippingError{
		{"an unknown priority name", []string{"  min_priority: warn # bad"}, "log_shipping.min_priority", ""},
		{"a priority name in upper case", []string{"  min_priority: INFO # bad"}, "log_shipping.min_priority", ""},
		{"a priority number", []string{"  min_priority: '6' # bad"}, "log_shipping.min_priority", ""},
		{"an empty priority name", []string{"  min_priority: '' # bad"}, "log_shipping.min_priority", ""},
		{"a relative path", []string{"  files:", file("var/log/mail.log", "syslog")}, "log_shipping.files[0].path", ""},
		{"a path that is not clean", []string{"  files:", file("/var/log/../mail.log", "syslog")}, "log_shipping.files[0].path", ""},
		{"a path with a trailing slash", []string{"  files:", file("/var/log/", "syslog")}, "log_shipping.files[0].path", ""},
		{"a path with a double slash", []string{"  files:", file("/var//log/mail.log", "syslog")}, "log_shipping.files[0].path", ""},
		{"a path of more than 1,024 bytes", []string{"  files:", file("/"+strings.Repeat("a", 1024), "syslog")}, "log_shipping.files[0].path", ""},
		{"a path with U+0000", []string{"  files:", file(`"/var/log/S3NT1NEL\0x"`, "syslog")}, "log_shipping.files[0].path", ""},
		{"a path listed twice", []string{"  files:", "    - {path: /var/log/mail.log, format: syslog}", file("/var/log/mail.log", "syslog")}, "log_shipping.files[1].path", ""},
		{"more than 16 files", files17, "log_shipping.files", ""},
		{"a format of json", []string{"  files:", file("/var/log/mail.log", "json")}, "log_shipping.files[0].format", formatReason},
		{"a format in upper case", []string{"  files:", file("/var/log/mail.log", "SYSLOG")}, "log_shipping.files[0].format", formatReason},
		{"a format of nginx without -error", []string{"  files:", file("/var/log/mail.log", "nginx")}, "log_shipping.files[0].format", formatReason},
		{"a format of nginx-error in upper case", []string{"  files:", file("/var/log/mail.log", "NGINX-ERROR")}, "log_shipping.files[0].format", formatReason},
		{"an empty format", []string{"  files:", file("/var/log/mail.log", "''")}, "log_shipping.files[0].format", formatReason},
		{"a rule without a condition", []string{"  drop:", "    - {program: x}", "    - {program: ''} # bad"}, "log_shipping.drop[1]", ""},
		{"an unknown priority in a rule", []string{"  drop:", "    - {program: x, priority: S3NT1NEL} # bad"}, "log_shipping.drop[0].priority", ""},
		{"a log that is neither the journal nor a configured file", []string{"  drop:", "    - {log: /var/log/other.log} # bad"}, "log_shipping.drop[0].log", ""},
		{"more than 64 rules", rules65, "log_shipping.drop", ""},
		{"a pattern that does not compile", []string{"  drop:", "    - {message: '(S3NT1NEL'} # bad"}, "log_shipping.drop[0].message", patternReason},
		{"a pattern of more than 1,024 bytes", bad(overlong), "log_shipping.drop[0].message", patternReason},
		{"a pattern of 1,017 bytes that compiles to 226,002 instructions", bad(strings.Repeat(".{0,1000}", 113)), "log_shipping.drop[0].message", patternReason},
		{"a pattern of 401 instructions", bad(`\pL{0,199}y`), "log_shipping.drop[0].message", patternReason},
		{"a character class pattern of 401 instructions", bad(`[a-z]{398}y`), "log_shipping.drop[0].message", patternReason},
		{"patterns of 402 instructions together", []string{"  drop: # bad", ruleLine(`\pL{0,99}y`), ruleLine(`\pL{0,99}y`)}, "log_shipping.drop", togetherReason},
	}
}

func TestLoadAgent_LogShippingErrors(t *testing.T) {
	for _, tt := range shippingErrors() {
		t.Run(tt.name, func(t *testing.T) {
			doc := shippingDoc(tt.lines...)
			path := writeTemp(t, doc)
			if strings.Contains(tt.name, "U+0000") && !strings.Contains(doc, `\0`) {
				t.Fatalf("the document %q lost the escape of U+0000, want the input of the case", doc)
			}

			got, err := LoadAgent(path, agentEnv())

			if got != nil {
				t.Errorf("LoadAgent() succeeded, want nil on error")
			}
			requireShippingError(t, tt, path, err, markedLine(t, doc))
		})
	}
}

// requireShippingError checks that err is the *KeyError of the case at the marked line: the file, the line and the key with its index, a
// reason, and no value.
func requireShippingError(t *testing.T, tt shippingError, path string, err error, line int) {
	t.Helper()
	ke := requireKeyError(t, err, anyKey, line)
	if ke.Key != tt.key {
		t.Errorf("KeyError.Key = %q, want %q", ke.Key, tt.key)
	}
	if ke.Reason == "" || (tt.why != "" && ke.Reason != tt.why) {
		t.Errorf("KeyError.Reason = %q, want %q", ke.Reason, tt.why)
	}
	if ke.File != path {
		t.Errorf("KeyError.File = %q, want %q", ke.File, path)
	}
	requireNoLeakBesidesFile(t, err, path, sentinel)
}
