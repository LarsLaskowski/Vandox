package config

import (
	"path/filepath"
	"regexp/syntax"
	"slices"
	"strconv"
)

// LogShipping configures the log shipping collector.
type LogShipping struct {
	Journal     bool       `yaml:"journal"`
	MinPriority string     `yaml:"min_priority"`
	Files       []LogFile  `yaml:"files"`
	Drop        []DropRule `yaml:"drop"`
}

// LogFile is a log file to ship.
type LogFile struct {
	Path   string `yaml:"path"`
	Format string `yaml:"format"`
}

// DropRule drops the lines that match all of its conditions.
type DropRule struct {
	Log      string `yaml:"log"`
	Program  string `yaml:"program"`
	Priority string `yaml:"priority"`
	Message  string `yaml:"message"`
}

// Bounds of log_shipping.
const (
	// MaxLogFiles is the most files log_shipping.files names.
	MaxLogFiles = 16
	// MaxDropRules is the most rules log_shipping.drop holds.
	MaxDropRules = 64
	// MaxPatternBytes is the longest pattern of a drop rule.
	MaxPatternBytes = 1024
	// MaxPatternInsts is the most instructions the compiled program of one pattern holds.
	MaxPatternInsts = 400
	// MaxPatternInstsTotal is the most instructions the programs of all patterns hold together.
	MaxPatternInstsTotal = 400
	// MaxLogPathBytes is the longest path of a log file.
	MaxLogPathBytes = 1024
)

// Keys and values of log_shipping that are named in several places.
const (
	keyShippingFiles  = "log_shipping.files"
	keyShippingDrop   = "log_shipping.drop"
	journalLog        = "journal"
	priorityLevelsMax = 8
)

// Reasons log_shipping is refused with; none of them quotes a value.
const (
	priorityReason     = "must be one of emerg, alert, crit, err, warning, notice, info, debug"
	logFilesReason     = "must be a list of at most 16 files"
	logPathReason      = "must be an absolute, clean path of at most 1024 bytes (no trailing slash, no . or .. elements)"
	logPathTwiceReason = "listed twice"
	logFormatReason    = "must be one of syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error"
	dropRulesReason    = "must be a list of at most 64 rules"
	dropEmptyReason    = "must set at least one of log, program, priority and message"
	dropLogReason      = "must be journal or a path listed in log_shipping.files"
	dropPatternReason  = "must be an RE2 pattern of at most 1024 bytes and 400 compiled instructions"
	dropTogetherReason = "patterns must compile to at most 400 instructions together"
)

// logFormats are the formats a log file of log_shipping.files can have.
var logFormats = []string{"syslog", "plesk-panel", "sw-cp-server-error", "nginx-error", "apache-error"}

var priorityNames = [priorityLevelsMax]string{"emerg", "alert", "crit", "err", "warning", "notice", "info", "debug"}

// PriorityLevel returns the syslog priority (0 to 7) named name and whether name is one.
func PriorityLevel(name string) (uint8, bool) {
	i := slices.Index(priorityNames[:], name)
	if i < 0 {
		return 0, false
	}
	return uint8(i), true // #nosec G115 -- an index into a list of eight names
}

// validate checks log_shipping.
func (s *LogShipping) validate(file string, lines map[string]int) error {
	if _, ok := PriorityLevel(s.MinPriority); !ok {
		return keyError(file, lines, "log_shipping.min_priority", priorityReason)
	}
	if err := s.checkFiles(file, lines); err != nil {
		return err
	}
	return s.checkDrop(file, lines)
}

// elementError returns the error for the key of an element of a list, at the line of the key or else of the element.
func elementError(file string, lines map[string]int, element, key, reason string) error {
	line := lines[key]
	if line == 0 {
		line = lines[element]
	}
	return &KeyError{File: file, Line: line, Key: key, Reason: reason}
}

// checkFiles checks log_shipping.files.
func (s *LogShipping) checkFiles(file string, lines map[string]int) error {
	if len(s.Files) > MaxLogFiles {
		return keyError(file, lines, keyShippingFiles, logFilesReason)
	}
	for i, f := range s.Files {
		element := keyShippingFiles + "[" + strconv.Itoa(i) + "]"
		switch {
		case !validLogPath(f.Path):
			return elementError(file, lines, element, element+".path", logPathReason)
		case slices.ContainsFunc(s.Files[:i], func(o LogFile) bool { return o.Path == f.Path }):
			return elementError(file, lines, element, element+".path", logPathTwiceReason)
		case f.Format != logFormats[0]:
			return elementError(file, lines, element, element+".format", logFormatReason)
		}
	}
	return nil
}

// validLogPath reports whether path is an absolute, clean path of 2 to 1,024 bytes.
func validLogPath(path string) bool {
	return len(path) >= 2 && len(path) <= MaxLogPathBytes && filepath.IsAbs(path) && filepath.Clean(path) == path
}

// checkDrop checks log_shipping.drop.
func (s *LogShipping) checkDrop(file string, lines map[string]int) error {
	if len(s.Drop) > MaxDropRules {
		return keyError(file, lines, keyShippingDrop, dropRulesReason)
	}
	total := 0
	for i, r := range s.Drop {
		element := keyShippingDrop + "[" + strconv.Itoa(i) + "]"
		if err := s.checkRule(file, lines, element, r); err != nil {
			return err
		}
		if r.Message == "" {
			continue
		}
		insts, ok := patternInstructions(r.Message)
		if !ok {
			return elementError(file, lines, element, element+".message", dropPatternReason)
		}
		total += insts
	}
	if total > MaxPatternInstsTotal {
		return keyError(file, lines, keyShippingDrop, dropTogetherReason)
	}
	return nil
}

// checkRule checks the conditions of one drop rule other than the pattern.
func (s *LogShipping) checkRule(file string, lines map[string]int, element string, r DropRule) error {
	if r.Log == "" && r.Program == "" && r.Priority == "" && r.Message == "" {
		return elementError(file, lines, element, element, dropEmptyReason)
	}
	if r.Log != "" && r.Log != journalLog && !slices.ContainsFunc(s.Files, func(f LogFile) bool { return f.Path == r.Log }) {
		return elementError(file, lines, element, element+".log", dropLogReason)
	}
	if _, ok := PriorityLevel(r.Priority); r.Priority != "" && !ok {
		return elementError(file, lines, element, element+".priority", priorityReason)
	}
	return nil
}

// patternInstructions returns the number of instructions of the program the pattern compiles to, as regexp.Compile builds it (Perl
// syntax, simplified); false means the pattern is longer than 1,024 bytes, does not compile or its program holds more than 400
// instructions. The program of a pattern is counted before any regular expression is built, because matching costs about as much per
// line as the program is long.
func patternInstructions(pattern string) (int, bool) {
	if len(pattern) > MaxPatternBytes {
		return 0, false
	}
	re, err := syntax.Parse(pattern, syntax.Perl)
	if err != nil {
		return 0, false
	}
	prog, err := syntax.Compile(re.Simplify())
	if err != nil || len(prog.Inst) > MaxPatternInsts {
		return 0, false
	}
	return len(prog.Inst), true
}
