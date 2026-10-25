package config

import (
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

	keyBackendBandwidth = "backend.backfill_bandwidth"
)

// Agent is the configuration of vandox-agent.
type Agent struct {
	AgentID    string        `yaml:"agent_id"`
	Backend    BackendTarget `yaml:"backend"`
	Spool      Spool         `yaml:"spool"`
	Log        Log           `yaml:"log"`
	Filesystem Filesystem    `yaml:"filesystem"`
	Processes  Processes     `yaml:"processes"`
	Backups    Backups       `yaml:"backups"`
	Services   Services      `yaml:"services"`
	MariaDB    MariaDB       `yaml:"mariadb"`
	// LogShipping configures the log shipping collector.
	LogShipping LogShipping  `yaml:"log_shipping"`
	Secrets     AgentSecrets `yaml:"-"`
}

// BackendTarget is where the agent sends its data.
type BackendTarget struct {
	URL string `yaml:"url"`
	// BackfillBandwidth is the bandwidth of the backfill as written, such as 256KiB.
	BackfillBandwidth string `yaml:"backfill_bandwidth"`
	// BackfillBytes is BackfillBandwidth in bytes per second; it is set by validation.
	BackfillBytes int64 `yaml:"-"`
}

// Spool configures the local spool.
type Spool struct {
	Directory string `yaml:"directory"`
	// MaxSize is the size cap of the spool as written, such as 2GiB.
	MaxSize string `yaml:"max_size"`
	// MinFree is the free space that stays available on the spool's file system as written, such as 1GiB.
	MinFree string `yaml:"min_free"`
	// MaxBytes is MaxSize in bytes; it is set by validation.
	MaxBytes int64 `yaml:"-"`
	// MinFreeBytes is MinFree in bytes; it is set by validation.
	MinFreeBytes int64 `yaml:"-"`
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

// Backups configures the backup run collector.
type Backups struct {
	Processes []BackupRule `yaml:"processes"`
}

// BackupRule names the processes of a backup by command or command line prefix.
type BackupRule struct {
	Command       string `yaml:"command"`
	CmdlinePrefix string `yaml:"cmdline_prefix"`
}

// checkBackupRules checks the list of backups.processes.
func checkBackupRules(file string, lines map[string]int, rules []BackupRule) error {
	if len(rules) > MaxProgramRules {
		return keyError(file, lines, "backups.processes", programRulesReason)
	}
	for i := range rules {
		path := "backups.processes[" + strconv.Itoa(i) + "]"
		r := &rules[i]
		fail := func(key, reason string) error {
			line := lines[key]
			if line == 0 {
				line = lines[path]
			}
			return &KeyError{File: file, Line: line, Key: key, Reason: reason}
		}
		switch {
		case (r.Command == "") == (r.CmdlinePrefix == ""):
			return fail(path, programOneOfReason)
		case len(r.Command) > MaxRuleMatchBytes:
			return fail(path+".command", programLengthReason)
		case len(r.CmdlinePrefix) > MaxRuleMatchBytes:
			return fail(path+".cmdline_prefix", programLengthReason)
		}
	}
	return nil
}

// Services configures the systemd service collector.
type Services struct {
	Units []string `yaml:"units"` // full service unit names or patterns, in file order
}

// MariaDB configures the MariaDB collector.
type MariaDB struct {
	Enabled bool   `yaml:"enabled"`
	Socket  string `yaml:"socket"`
}

// Defaults and bounds of mariadb.socket.
const (
	DefaultMariaDBSocket = "/run/mysqld/mysqld.sock"
	MaxSocketPathBytes   = 107
)

// socketPathReason is the reason mariadb.socket is refused with; it does not quote the value.
const socketPathReason = "must be an absolute, clean path of at most 107 bytes"

// checkSocketPath checks the value of key: an absolute, clean path of at most MaxSocketPathBytes bytes.
func checkSocketPath(file string, lines map[string]int, key, value string) error {
	if len(value) < 2 || len(value) > MaxSocketPathBytes || !filepath.IsAbs(value) || filepath.Clean(value) != value {
		return keyError(file, lines, key, socketPathReason)
	}
	return nil
}

// Bounds of services.units.
const (
	MaxServiceUnits     = 64
	MaxServiceUnitBytes = 255
)

// Reasons services.units is refused with; none of them quotes a value.
const (
	serviceUnitsReason  = "must be a list of at most 64 unit names"
	serviceNameReason   = "must be a service unit name or pattern of 1 to 255 bytes of [A-Za-z0-9:_.@*?-], not starting with . or -"
	serviceTypeReason   = "must name a service unit"
	serviceTemplateText = "must name an instance, not a template"
	serviceTwiceReason  = "listed twice"
)

// serviceSuffix is the type suffix of a service unit; serviceOtherTypes are the suffixes of the other unit types.
const serviceSuffix = ".service"

var serviceOtherTypes = []string{
	".socket", ".timer", ".target", ".mount", ".automount", ".swap", ".path", ".slice", ".scope", ".device",
}

// DefaultServiceUnits returns the default value of services.units.
func DefaultServiceUnits() []string {
	return []string{
		"sw-cp-server.service", "sw-engine.service", "psa.service", "nginx.service", "apache2.service", "postfix.service",
		"dovecot.service", "mariadb.service", "plesk-php*-fpm.service", "php*-fpm.service", "fail2ban.service",
	}
}

// checkServiceUnits checks the list of services.units and returns it with every entry as a full unit name.
func checkServiceUnits(file string, lines map[string]int, values []string) ([]string, error) {
	const key = "services.units"
	if len(values) > MaxServiceUnits {
		return nil, keyError(file, lines, key, serviceUnitsReason)
	}
	names := make([]string, 0, len(values))
	for i, v := range values {
		elem := key + "[" + strconv.Itoa(i) + "]"
		name, reason := serviceUnitName(v)
		if reason == "" && slices.Contains(names, name) {
			reason = serviceTwiceReason
		}
		if reason != "" {
			return nil, keyError(file, lines, elem, reason)
		}
		names = append(names, name)
	}
	return names, nil
}

// serviceUnitName returns the full unit name of entry, or the reason it is refused.
func serviceUnitName(entry string) (name, reason string) {
	if entry == "" || entry[0] == '.' || entry[0] == '-' || strings.Count(entry, "@") > 1 {
		return "", serviceNameReason
	}
	for i := range len(entry) {
		c := entry[i]
		if (c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && strings.IndexByte(":_.@*?-", c) < 0 {
			return "", serviceNameReason
		}
	}
	if entry[0] == '@' {
		return "", serviceTemplateText
	}
	if dot := strings.LastIndexByte(entry, '.'); dot >= 0 && slices.Contains(serviceOtherTypes, entry[dot:]) {
		return "", serviceTypeReason
	}
	name = entry
	if !strings.HasSuffix(entry, serviceSuffix) {
		name += serviceSuffix
	}
	if len(name) > MaxServiceUnitBytes {
		return "", serviceNameReason
	}
	if strings.HasSuffix(name, "@"+serviceSuffix) {
		return "", serviceTemplateText
	}
	return name, ""
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
		Spool: Spool{
			Directory:    "/var/lib/vandox/spool",
			MaxSize:      "2GiB",
			MinFree:      "1GiB",
			MaxBytes:     2 << 30,
			MinFreeBytes: 1 << 30,
		},
		Backend: BackendTarget{BackfillBandwidth: "256KiB", BackfillBytes: 256 << 10},
		Log:     Log{Level: "info"},

		Filesystem: Filesystem{MountPoints: []string{"/"}},
		Backups:    Backups{Processes: []BackupRule{{Command: "pzstd"}}},
		Services:   Services{Units: DefaultServiceUnits()},
		MariaDB:    MariaDB{Enabled: true, Socket: DefaultMariaDBSocket},

		LogShipping: LogShipping{Journal: true, MinPriority: "info"},
	}
}

// AgentKeys returns every option key path in file order: agent_id, backend.url, backend.backfill_bandwidth, spool.directory, spool.max_size, spool.min_free, log.level,
// filesystem.mount_points, processes.programs, services.units, mariadb.enabled, mariadb.socket, backups.processes and the log_shipping
// options journal, min_priority, files and drop.
func AgentKeys() []string {
	return []string{
		keyAgentID, keyBackendURL, keyBackendBandwidth, "spool.directory", "spool.max_size", "spool.min_free", "log.level", "filesystem.mount_points", "processes.programs", "services.units",
		"mariadb.enabled", "mariadb.socket", "backups.processes",
		"log_shipping.journal", "log_shipping.min_priority", keyShippingFiles, keyShippingDrop,
	}
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
	var err error
	if a.Backend.BackfillBytes, err = checkBandwidth(file, lines, keyBackendBandwidth, a.Backend.BackfillBandwidth); err != nil {
		return err
	}
	if err := checkDirectory(file, lines, "spool.directory", a.Spool.Directory); err != nil {
		return err
	}
	if a.Spool.MaxBytes, err = checkSize(file, lines, "spool.max_size", a.Spool.MaxSize, minSpoolMax, maxSpoolMax, spoolMaxText); err != nil {
		return err
	}
	if a.Spool.MinFreeBytes, err = checkSize(file, lines, "spool.min_free", a.Spool.MinFree, 0, maxSpoolMinFree, spoolMinFreeText); err != nil {
		return err
	}
	if err := checkLogLevel(file, lines, "log.level", a.Log.Level); err != nil {
		return err
	}
	if err := checkMountPoints(file, lines, "filesystem.mount_points", a.Filesystem.MountPoints); err != nil {
		return err
	}
	if err := checkProgramRules(file, lines, a.Processes.Programs); err != nil {
		return err
	}
	units, err := checkServiceUnits(file, lines, a.Services.Units)
	if err != nil {
		return err
	}
	a.Services.Units = units
	if err := checkSocketPath(file, lines, "mariadb.socket", a.MariaDB.Socket); err != nil {
		return err
	}
	if err := checkBackupRules(file, lines, a.Backups.Processes); err != nil {
		return err
	}
	return a.LogShipping.validate(file, lines)
}

// Bounds of spool.max_size and spool.min_free in bytes.
const (
	minSpoolMax     = 64 << 20
	maxSpoolMax     = 64 << 30
	maxSpoolMinFree = 1024 << 30
)

// Reasons spool.max_size and spool.min_free are refused with; none of them quotes a value.
const (
	spoolMaxText     = "must be a whole number of MiB or GiB from 64MiB to 64GiB, such as 512MiB or 2GiB"
	spoolMinFreeText = "must be a whole number of MiB or GiB from 0MiB to 1024GiB, such as 0MiB or 1GiB"
)

// Bounds of backend.backfill_bandwidth in bytes per second and the reason it is refused with.
const (
	minBackfillBandwidth  = 16 << 10
	maxBackfillBandwidth  = 1024 << 20
	backfillBandwidthText = "must be a whole number of KiB or MiB from 16KiB to 1024MiB, such as 256KiB or 4MiB"
)

// checkBandwidth parses the bandwidth value of key and checks that it lies between 16KiB and 1024MiB.
func checkBandwidth(file string, lines map[string]int, key, value string) (int64, error) {
	n, ok := parseBandwidth(value)
	if ok && n >= minBackfillBandwidth && n <= maxBackfillBandwidth {
		return n, nil
	}
	return 0, keyError(file, lines, key, backfillBandwidthText)
}

// checkSize parses the size value of key and checks that it lies between lo and hi bytes.
func checkSize(file string, lines map[string]int, key, value string, lo, hi int64, reason string) (int64, error) {
	n, ok := parseSize(value)
	if ok && n >= lo && n <= hi {
		return n, nil
	}
	return 0, keyError(file, lines, key, reason)
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
