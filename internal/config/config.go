// Package config loads the configuration of vandox-agent and vandoxd: one strictly parsed YAML file per
// binary for the options, and the environment (directly or through *_FILE files) for the secrets.
package config

import (
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strconv"
	"strings"
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

// Error returns "config: <File>:<Line>: <Key>: <Reason>". The line is left out when it is 0 and the key
// when it is empty.
func (e *KeyError) Error() string {
	var b strings.Builder
	b.WriteString("config: ")
	b.WriteString(e.File)
	if e.Line > 0 {
		b.WriteString(":")
		b.WriteString(strconv.Itoa(e.Line))
	}
	b.WriteString(": ")
	if e.Key != "" {
		b.WriteString(e.Key)
		b.WriteString(": ")
	}
	b.WriteString(e.Reason)
	return b.String()
}

// keyError returns a *KeyError for key, at the line the decoder recorded for it (0 when it was not set).
func keyError(file string, lines map[string]int, key, reason string) *KeyError {
	return &KeyError{File: file, Line: lines[key], Key: key, Reason: reason}
}

// requireKey fails when the file did not set key.
func requireKey(file string, lines map[string]int, key string) error {
	if lines[key] == 0 {
		return &KeyError{File: file, Key: key, Reason: "required key is missing"}
	}
	return nil
}

// readFile reads the regular file at path, at most limit bytes. The path is checked before it is opened,
// so a FIFO cannot block the read.
func readFile(path string, limit int64) ([]byte, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, fmt.Errorf("config: %w", err)
	}
	if !info.Mode().IsRegular() {
		return nil, fmt.Errorf("config: %s: not a regular file", path)
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("config: %w", err)
	}
	defer func() { _ = f.Close() }()
	data, err := io.ReadAll(io.LimitReader(f, limit+1))
	if err != nil {
		return nil, fmt.Errorf("config: %s: %w", path, err)
	}
	if int64(len(data)) > limit {
		return nil, fmt.Errorf("config: %s: file is larger than %d bytes", path, limit)
	}
	return data, nil
}

// checkDirectory checks that value is an absolute, clean directory path.
func checkDirectory(file string, lines map[string]int, key, value string) error {
	if !filepath.IsAbs(value) || filepath.Clean(value) != value {
		return keyError(file, lines, key, "must be an absolute, clean path (no trailing slash, no . or .. elements)")
	}
	return nil
}

// checkListen checks that value is a listen address with a port of 1 to 65535 and returns the port.
func checkListen(file string, lines map[string]int, key, value string) (port uint16, err error) {
	const reason = "must be [host]:port with an empty host or an IP address and a port of 1 to 65535"
	host, portText, splitErr := net.SplitHostPort(value)
	if splitErr != nil {
		return 0, keyError(file, lines, key, reason)
	}
	if host != "" {
		if _, parseErr := netip.ParseAddr(host); parseErr != nil {
			return 0, keyError(file, lines, key, reason)
		}
	}
	port, ok := parsePort(portText)
	if !ok {
		return 0, keyError(file, lines, key, reason)
	}
	return port, nil
}

// parsePort converts 1 to 5 ASCII digits to a port of 1 to 65535. A sign, a space or a name is not accepted.
func parsePort(text string) (uint16, bool) {
	if len(text) < 1 || len(text) > 5 {
		return 0, false
	}
	for i := 0; i < len(text); i++ {
		if text[i] < '0' || text[i] > '9' {
			return 0, false
		}
	}
	n, err := strconv.ParseUint(text, 10, 16)
	if err != nil || n == 0 {
		return 0, false
	}
	return uint16(n), true
}

// checkLogLevel checks that value is one of debug, info, warn and error.
func checkLogLevel(file string, lines map[string]int, key, value string) error {
	switch value {
	case "debug", "info", "warn", "error":
		return nil
	}
	return keyError(file, lines, key, "must be one of debug, info, warn, error")
}
