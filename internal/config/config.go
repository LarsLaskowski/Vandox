// Package config loads the configuration of vandox-agent and vandoxd: one strictly parsed YAML file per
// binary for the options, and the environment (directly or through *_FILE files) for the secrets.
package config

import (
	"errors"
	"fmt"
)

// File locations, limits and environment variables.
const (
	// DefaultAgentFile is the default location of the agent configuration file.
	DefaultAgentFile = "/etc/vandox/agent.yaml"
	// DefaultBackendFile is the default location of the backend configuration file.
	DefaultBackendFile = "/etc/vandox/vandoxd.yaml"

	// MaxFileBytes is the largest configuration file read.
	MaxFileBytes = 1 << 20
	// MaxSecretBytes is the largest secret value, also the largest *_FILE content (plus line ending).
	MaxSecretBytes = 4096
	// MinAgentTokenBytes is the shortest accepted agent token.
	MinAgentTokenBytes = 32

	// EnvAgentToken is the environment variable of the agent token.
	EnvAgentToken = "VANDOX_AGENT_TOKEN"
	// EnvWebPasswordHash is the environment variable of the web UI password hash.
	EnvWebPasswordHash = "VANDOX_WEB_PASSWORD_HASH"
	// EnvTelegramBotToken is the environment variable of the Telegram bot token.
	EnvTelegramBotToken = "VANDOX_TELEGRAM_BOT_TOKEN"
	// FileSuffix is appended to a secret's variable name to name the variable that holds a file path.
	FileSuffix = "_FILE"
)

// KeyError reports a problem in the configuration file, at a key or at a line.
type KeyError struct {
	File   string // path as given to the loader
	Line   int    // 1-based; 0 when the key is missing or the YAML parser reported no line
	Key    string // dotted key path, e.g. "backend.url"; empty for document-level problems and parser errors
	Reason string // never contains the value or any other document text
}

// Error returns "config: <File>:<Line>: <Key>: <Reason>".
func (e *KeyError) Error() string {
	return fmt.Sprintf("config: %s: %s", e.File, e.Reason)
}

var errNotImplemented = errors.New("not implemented")

// readFile reads the regular file at path, at most limit bytes.
func readFile(path string, limit int64) ([]byte, error) {
	return nil, errNotImplemented
}

// checkDirectory checks that value is an absolute, clean directory path.
func checkDirectory(file string, lines map[string]int, key, value string) error {
	return errNotImplemented
}

// checkListen checks that value is a listen address with a port of 1 to 65535 and returns the port.
func checkListen(file string, lines map[string]int, key, value string) (port uint16, err error) {
	return 0, errNotImplemented
}

// checkLogLevel checks that value is one of debug, info, warn and error.
func checkLogLevel(file string, lines map[string]int, key, value string) error {
	return errNotImplemented
}
