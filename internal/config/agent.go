package config

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
	return Agent{}
}

// AgentKeys returns every option key path in file order: agent_id, backend.url, spool.directory, log.level.
func AgentKeys() []string {
	return nil
}

// LoadAgent reads the agent configuration file at path and the secrets from environ (in os.Environ form).
func LoadAgent(path string, environ []string) (*Agent, error) {
	return nil, errNotImplemented
}
