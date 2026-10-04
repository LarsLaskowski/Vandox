package config

import (
	"fmt"
	"log/slog"
)

// Secret holds a secret value and never reveals it through formatting, logging or marshaling.
type Secret struct {
	value string
}

// Value returns the secret value. It is the only accessor.
func (s Secret) Value() string { return "" }

// IsSet reports whether the secret holds a value.
func (s Secret) IsSet() bool { return false }

// String returns "[redacted]".
func (s Secret) String() string { return "" }

// Format writes "[redacted]" for every verb.
func (s Secret) Format(f fmt.State, verb rune) {}

// LogValue returns slog.StringValue("[redacted]").
func (s Secret) LogValue() slog.Value { return slog.Value{} }

// MarshalText returns []byte("[redacted]").
func (s Secret) MarshalText() ([]byte, error) { return nil, errNotImplemented }

// SecretError reports a problem with a secret's environment variable or file.
type SecretError struct {
	Var    string // e.g. "VANDOX_AGENT_TOKEN_FILE"; empty for an unknown name that is not shown
	Reason string // never contains the value, a *_FILE path or the file content
	Err    error  // only the errno of an *fs.PathError, never the PathError; may be nil
}

// Error returns "config: <Var>: <Reason>[: <Err>]", or "config: <Reason>[: <Err>]" if Var is empty.
func (e *SecretError) Error() string { return e.Reason }

// Unwrap returns Err.
func (e *SecretError) Unwrap() error { return e.Err }

// checkEnviron rejects unknown and duplicate VANDOX_ variables in environ and returns the known ones.
// known holds the full variable names including the _FILE forms.
func checkEnviron(environ []string, known []string) (map[string]string, error) {
	return nil, errNotImplemented
}

// readSecret reads the secret called name from env, directly or through its _FILE variable.
func readSecret(env map[string]string, name string) (Secret, error) {
	return Secret{}, errNotImplemented
}
