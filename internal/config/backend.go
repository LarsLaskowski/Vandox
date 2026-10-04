package config

// Backend is the configuration of vandoxd.
type Backend struct {
	Web     Listener       `yaml:"web"`
	Ingest  Listener       `yaml:"ingest"`
	Storage Storage        `yaml:"storage"`
	Log     Log            `yaml:"log"`
	Secrets BackendSecrets `yaml:"-"`
}

// Listener is a listen address.
type Listener struct {
	Listen string `yaml:"listen"`
}

// Storage configures where the backend keeps its data.
type Storage struct {
	Directory string `yaml:"directory"`
}

// BackendSecrets holds the secrets of the backend.
type BackendSecrets struct {
	AgentToken       Secret // optional at load; #40 decides behavior when unset
	WebPasswordHash  Secret // optional at load; #25 refuses to start the UI without it
	TelegramBotToken Secret // optional at load; #60
}

// DefaultBackend returns the backend configuration with every key at its default.
func DefaultBackend() Backend {
	return Backend{}
}

// BackendKeys returns every option key path in file order: web.listen, ingest.listen, storage.directory,
// log.level.
func BackendKeys() []string {
	return nil
}

// LoadBackend reads the backend configuration file at path and the secrets from environ (in os.Environ
// form).
func LoadBackend(path string, environ []string) (*Backend, error) {
	return nil, errNotImplemented
}
