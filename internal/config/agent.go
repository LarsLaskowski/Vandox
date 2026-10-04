package config

import (
	"net/netip"
	"net/url"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// Agent is the configuration of vandox-agent.
type Agent struct {
	AgentID string        `yaml:"agent_id"`
	Backend BackendTarget `yaml:"backend"`
	Spool   Spool         `yaml:"spool"`
	Log     Log           `yaml:"log"`
	Secrets AgentSecrets  `yaml:"-"`
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

// AgentSecrets holds the secrets of the agent.
type AgentSecrets struct {
	AgentToken Secret
}

// DefaultAgent returns the agent configuration with every optional key at its default.
func DefaultAgent() Agent {
	return Agent{
		Spool: Spool{Directory: "/var/lib/vandox/spool"},
		Log:   Log{Level: "info"},
	}
}

// AgentKeys returns every option key path in file order: agent_id, backend.url, spool.directory, log.level.
func AgentKeys() []string {
	return []string{"agent_id", "backend.url", "spool.directory", "log.level"}
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
	if err := requireKey(file, lines, "agent_id"); err != nil {
		return err
	}
	if wire.ValidateAgentID(a.AgentID) != nil {
		return keyError(file, lines, "agent_id", "must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit")
	}
	if err := requireKey(file, lines, "backend.url"); err != nil {
		return err
	}
	if reason := urlProblem(a.Backend.URL); reason != "" {
		return keyError(file, lines, "backend.url", reason)
	}
	if err := checkDirectory(file, lines, "spool.directory", a.Spool.Directory); err != nil {
		return err
	}
	return checkLogLevel(file, lines, "log.level", a.Log.Level)
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
