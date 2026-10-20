package config

import (
	"errors"
	"net/netip"
	"net/url"
	"path/filepath"
	"slices"
	"strconv"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// Keys of the agent configuration that are named in several places.
const (
	keyAgentID    = "agent_id"
	keyBackendURL = "backend.url"
)

// Agent is the configuration of vandox-agent.
type Agent struct {
	AgentID    string        `yaml:"agent_id"`
	Backend    BackendTarget `yaml:"backend"`
	Spool      Spool         `yaml:"spool"`
	Log        Log           `yaml:"log"`
	Filesystem Filesystem    `yaml:"filesystem"`
	Processes  Processes     `yaml:"processes"`
	Services   Services      `yaml:"services"`
	Secrets    AgentSecrets  `yaml:"-"`
}

// BackendTarget is where the agent sends its data.
type BackendTarget struct {
	URL string `yaml:"url"`
}

// Spool configures the local spool.
type Spool struct {
	Directory string `yaml:"directory"`
}

// Log configures logging.
type Log struct {
	Level string `yaml:"level"`
}

// Filesystem configures the file system metrics.
type Filesystem struct {
	MountPoints []string `yaml:"mount_points"`
}

// Processes configures the process collector.
type Processes struct {
	Programs []ProgramRule `yaml:"programs"`
}

// ProgramRule names the program of the processes whose command and command line match.
type ProgramRule struct {
	Program        string `yaml:"program"`
	Command        string `yaml:"command"`
	CmdlinePrefix  string `yaml:"cmdline_prefix"`
	AppendNextWord bool   `yaml:"append_next_word"`
}

// Services configures the systemd service collector.
type Services struct {
	Units []string `yaml:"units"` // full service unit names or patterns, in file order
}

// Bounds of services.units.
const (
	MaxServiceUnits     = 64
	MaxServiceUnitBytes = 255
)

// DefaultServiceUnits returns the default value of services.units.
func DefaultServiceUnits() []string {
	return nil
}

// checkServiceUnits checks the list of services.units and returns it with every entry as a full unit name.
func checkServiceUnits(file string, lines map[string]int, values []string) ([]string, error) {
	return nil, errors.New("not implemented")
}

// serviceUnitName returns the full unit name of entry, or the reason it is refused.
func serviceUnitName(entry string) (name, reason string) {
	return "", "not implemented"
}

// Bounds of processes.programs.
const (
	MaxProgramRules   = 64
	MaxRuleMatchBytes = 256
)

// Reasons processes.programs is refused with; none of them quotes a value.
const (
	programRulesReason  = "must be a list of at most 64 rules"
	programNameReason   = "must be 1 to 128 characters of [A-Za-z0-9._:/@+-], starting with a letter or digit"
	programOneOfReason  = "must set exactly one of command and cmdline_prefix"
	programLengthReason = "must be 1 to 256 bytes"
	programWordReason   = "needs cmdline_prefix"
)

// maxProgramNameBytes is the longest program a rule names.
const maxProgramNameBytes = 128

// checkProgramRules checks the list of processes.programs.
func checkProgramRules(file string, lines map[string]int, rules []ProgramRule) error {
	if len(rules) > MaxProgramRules {
		return keyError(file, lines, "processes.programs", programRulesReason)
	}
	for i := range rules {
		if err := checkProgramRule(file, lines, "processes.programs["+strconv.Itoa(i)+"]", &rules[i]); err != nil {
			return err
		}
	}
	return nil
}

// checkProgramRule checks the rule r, whose key path is path.
func checkProgramRule(file string, lines map[string]int, path string, r *ProgramRule) error {
	fail := func(key, reason string) error {
		line := lines[key]
		if line == 0 {
			line = lines[path]
		}
		return &KeyError{File: file, Line: line, Key: key, Reason: reason}
	}
	if !validProgramName(r.Program) {
		return fail(path+".program", programNameReason)
	}
	if (r.Command == "") == (r.CmdlinePrefix == "") {
		return fail(path, programOneOfReason)
	}
	if len(r.Command) > MaxRuleMatchBytes {
		return fail(path+".command", programLengthReason)
	}
	if len(r.CmdlinePrefix) > MaxRuleMatchBytes {
		return fail(path+".cmdline_prefix", programLengthReason)
	}
	if r.AppendNextWord && r.CmdlinePrefix == "" {
		return fail(path+".append_next_word", programWordReason)
	}
	return nil
}

// validProgramName reports whether s is 1 to 128 characters of [A-Za-z0-9._:/@+-], starting with a letter or digit.
func validProgramName(s string) bool {
	if len(s) < 1 || len(s) > maxProgramNameBytes {
		return false
	}
	for i := 0; i < len(s); i++ {
		c := s[i]
		alnum := c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9'
		if !alnum && (i == 0 || !strings.ContainsRune("._:/@+-", rune(c))) {
			return false
		}
	}
	return true
}

// mountPointsRule is the reason filesystem.mount_points is refused with; it does not quote the value.
const mountPointsRule = "must be a list of at most 16 absolute, clean paths of at most 1024 bytes each " +
	"(no trailing slash, no . or .. elements), each listed once"

// Bounds of filesystem.mount_points.
const (
	MaxMountPoints     = 16
	MaxMountPointBytes = 1024
)

// AgentSecrets holds the secrets of the agent.
type AgentSecrets struct {
	AgentToken Secret
}

// DefaultAgent returns the agent configuration with every optional key at its default.
func DefaultAgent() Agent {
	return Agent{
		Spool: Spool{Directory: "/var/lib/vandox/spool"},
		Log:   Log{Level: "info"},

		Filesystem: Filesystem{MountPoints: []string{"/"}},
	}
}

// AgentKeys returns every option key path in file order: agent_id, backend.url, spool.directory, log.level,
// filesystem.mount_points, processes.programs.
func AgentKeys() []string {
	return []string{keyAgentID, keyBackendURL, "spool.directory", "log.level", "filesystem.mount_points", "processes.programs"}
}

// LoadAgent reads the agent configuration file at path and the secrets from environ (in os.Environ form).
func LoadAgent(path string, environ []string) (*Agent, error) {
	data, err := readFile(path, MaxFileBytes)
	if err != nil {
		return nil, err
	}
	cfg := DefaultAgent()
	lines, err := decodeStrict(path, data, &cfg)
	if err != nil {
		return nil, err
	}
	if err := cfg.validate(path, lines); err != nil {
		return nil, err
	}
	env, err := checkEnviron(environ, []string{EnvAgentToken, EnvAgentToken + FileSuffix})
	if err != nil {
		return nil, err
	}
	if cfg.Secrets.AgentToken, err = readAgentToken(env, true); err != nil {
		return nil, err
	}
	return &cfg, nil
}

// validate checks the options in struct field order.
func (a *Agent) validate(file string, lines map[string]int) error {
	if err := requireKey(file, lines, keyAgentID); err != nil {
		return err
	}
	if wire.ValidateAgentID(a.AgentID) != nil {
		return keyError(file, lines, keyAgentID, "must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit")
	}
	if err := requireKey(file, lines, keyBackendURL); err != nil {
		return err
	}
	if reason := urlProblem(a.Backend.URL); reason != "" {
		return keyError(file, lines, keyBackendURL, reason)
	}
	if err := checkDirectory(file, lines, "spool.directory", a.Spool.Directory); err != nil {
		return err
	}
	if err := checkLogLevel(file, lines, "log.level", a.Log.Level); err != nil {
		return err
	}
	if err := checkMountPoints(file, lines, "filesystem.mount_points", a.Filesystem.MountPoints); err != nil {
		return err
	}
	return checkProgramRules(file, lines, a.Processes.Programs)
}

// checkMountPoints checks the list of filesystem.mount_points.
func checkMountPoints(file string, lines map[string]int, key string, values []string) error {
	if len(values) > MaxMountPoints {
		return keyError(file, lines, key, mountPointsRule)
	}
	for i, v := range values {
		if len(v) < 1 || len(v) > MaxMountPointBytes || !filepath.IsAbs(v) || filepath.Clean(v) != v || slices.Contains(values[:i], v) {
			return keyError(file, lines, key, mountPointsRule)
		}
	}
	return nil
}

// urlRules are the rules of backend.url, in the order they are checked. Each returns the reason it is
// violated or "". None of them quotes the value, because it could hold a credential.
var urlRules = []func(u *url.URL) string{
	func(u *url.URL) string {
		if u.Scheme != "http" && u.Scheme != "https" {
			return "scheme must be http or https"
		}
		return ""
	},
	func(u *url.URL) string {
		if u.Opaque != "" || u.User != nil {
			return "must not hold user info or an opaque part"
		}
		return ""
	},
	func(u *url.URL) string {
		if u.Hostname() == "" || strings.HasSuffix(u.Host, ":") {
			return "must have a host and, if a colon is given, a port"
		}
		return ""
	},
	func(u *url.URL) string {
		if !validHost(u.Hostname()) {
			return "host must be an IP address or a name of [A-Za-z0-9.-]"
		}
		return ""
	},
	func(u *url.URL) string {
		if _, ok := parsePort(u.Port()); u.Port() != "" && !ok {
			return "port must be 1 to 65535"
		}
		return ""
	},
	func(u *url.URL) string {
		if u.RawQuery != "" || u.ForceQuery || u.Fragment != "" || u.RawFragment != "" {
			return "must not hold a query or a fragment"
		}
		return ""
	},
	func(u *url.URL) string {
		if u.Path != "" && u.Path != "/" {
			return "must not hold a path"
		}
		return ""
	},
}

// urlProblem returns the first rule backend.url violates, or "". The error of url.Parse is dropped because
// its text quotes the URL.
func urlProblem(raw string) string {
	u, err := url.Parse(raw)
	if err != nil {
		return "must be a valid http or https URL"
	}
	for _, rule := range urlRules {
		if reason := rule(u); reason != "" {
			return reason
		}
	}
	return ""
}

// validHost reports whether host is an IP address or 1 to 253 bytes of [A-Za-z0-9.-].
func validHost(host string) bool {
	if _, err := netip.ParseAddr(host); err == nil {
		return true
	}
	if len(host) < 1 || len(host) > 253 {
		return false
	}
	for i := 0; i < len(host); i++ {
		c := host[i]
		if (c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && c != '.' && c != '-' {
			return false
		}
	}
	return true
}
