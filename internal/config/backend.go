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
	return Backend{
		Web:     Listener{Listen: ":8080"},
		Ingest:  Listener{Listen: ":8081"},
		Storage: Storage{Directory: "/data"},
		Log:     Log{Level: "info"},
	}
}

// BackendKeys returns every option key path in file order: web.listen, ingest.listen, storage.directory,
// log.level.
func BackendKeys() []string {
	return []string{"web.listen", "ingest.listen", "storage.directory", "log.level"}
}

// LoadBackend reads the backend configuration file at path and the secrets from environ (in os.Environ
// form).
func LoadBackend(path string, environ []string) (*Backend, error) {
	data, err := readFile(path, MaxFileBytes)
	if err != nil {
		return nil, err
	}
	cfg := DefaultBackend()
	lines, err := decodeStrict(path, data, &cfg)
	if err != nil {
		return nil, err
	}
	if err := cfg.validate(path, lines); err != nil {
		return nil, err
	}
	known := []string{
		EnvAgentToken, EnvAgentToken + FileSuffix,
		EnvWebPasswordHash, EnvWebPasswordHash + FileSuffix,
		EnvTelegramBotToken, EnvTelegramBotToken + FileSuffix,
	}
	env, err := checkEnviron(environ, known)
	if err != nil {
		return nil, err
	}
	if cfg.Secrets, err = readBackendSecrets(env); err != nil {
		return nil, err
	}
	return &cfg, nil
}

// validate checks the options in struct field order.
func (b *Backend) validate(file string, lines map[string]int) error {
	webPort, err := checkListen(file, lines, "web.listen", b.Web.Listen)
	if err != nil {
		return err
	}
	ingestPort, err := checkListen(file, lines, "ingest.listen", b.Ingest.Listen)
	if err != nil {
		return err
	}
	if webPort == ingestPort {
		return keyError(file, lines, "ingest.listen", "must not use the same port as web.listen")
	}
	if err := checkDirectory(file, lines, "storage.directory", b.Storage.Directory); err != nil {
		return err
	}
	return checkLogLevel(file, lines, "log.level", b.Log.Level)
}

// readBackendSecrets reads the three optional secrets of the backend.
func readBackendSecrets(env map[string]string) (BackendSecrets, error) {
	var s BackendSecrets
	var err error
	if s.AgentToken, err = readAgentToken(env, false); err != nil {
		return BackendSecrets{}, err
	}
	if s.WebPasswordHash, err = readSecret(env, EnvWebPasswordHash); err != nil {
		return BackendSecrets{}, err
	}
	if s.TelegramBotToken, err = readSecret(env, EnvTelegramBotToken); err != nil {
		return BackendSecrets{}, err
	}
	return s, nil
}
