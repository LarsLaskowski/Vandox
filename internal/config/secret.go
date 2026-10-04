package config

import (
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log/slog"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

const (
	redactedText = "[redacted]"
	envPrefix    = "VANDOX_"
	maxNameBytes = 64
)

// Secret holds a secret value and never reveals it through formatting, logging or marshaling.
//
// The value is stored behind a pointer on purpose: for a verb that does not apply to a struct, such as %p
// on a non-pointer, fmt skips the Formatter and prints the struct fields through reflection, which would
// show a string field. A pointer field is printed as an address only.
type Secret struct {
	value *string
}

// newSecret returns a Secret holding v.
func newSecret(v string) Secret { return Secret{value: &v} }

// Value returns the secret value, or "" if the secret is not set. It is the only accessor.
func (s Secret) Value() string {
	if s.value == nil {
		return ""
	}
	return *s.value
}

// IsSet reports whether the secret holds a value.
func (s Secret) IsSet() bool { return s.value != nil && *s.value != "" }

// String returns "[redacted]".
func (s Secret) String() string { return redactedText }

// Format writes "[redacted]" for every verb that fmt passes to the Formatter. A bad verb on a non-pointer
// value is not passed on, which the pointer in the value field covers.
func (s Secret) Format(f fmt.State, _ rune) { _, _ = io.WriteString(f, redactedText) }

// LogValue returns slog.StringValue("[redacted]").
func (s Secret) LogValue() slog.Value { return slog.StringValue(redactedText) }

// MarshalText returns []byte("[redacted]").
func (s Secret) MarshalText() ([]byte, error) { return []byte(redactedText), nil }

// SecretError reports a problem with a secret's environment variable or file.
type SecretError struct {
	Var    string // e.g. "VANDOX_AGENT_TOKEN_FILE"; empty for an unknown name that is not shown
	Reason string // never contains the value, a *_FILE path or the file content
	Err    error  // only the errno of an *fs.PathError, never the PathError; may be nil
}

// Error returns "config: <Var>: <Reason>[: <Err>]", or "config: <Reason>[: <Err>]" if Var is empty.
func (e *SecretError) Error() string {
	msg := "config: "
	if e.Var != "" {
		msg += e.Var + ": "
	}
	msg += e.Reason
	if e.Err != nil {
		msg += ": " + e.Err.Error()
	}
	return msg
}

// Unwrap returns Err.
func (e *SecretError) Unwrap() error { return e.Err }

// checkEnviron rejects unknown and duplicate VANDOX_ variables in environ and returns the known ones.
// known holds the full variable names including the _FILE forms.
func checkEnviron(environ []string, known []string) (map[string]string, error) {
	out := make(map[string]string, len(known))
	for _, entry := range environ {
		name, value, ok := strings.Cut(entry, "=")
		if !ok || !hasEnvPrefix(name) {
			continue
		}
		if !contains(known, name) {
			return nil, unknownVariable(name)
		}
		if _, dup := out[name]; dup {
			return nil, &SecretError{Var: name, Reason: "set more than once"}
		}
		out[name] = value
	}
	return out, nil
}

// hasEnvPrefix reports whether name starts with VANDOX_ in any letter case.
func hasEnvPrefix(name string) bool {
	return len(name) >= len(envPrefix) && strings.EqualFold(name[:len(envPrefix)], envPrefix)
}

// contains reports whether list holds s.
func contains(list []string, s string) bool {
	for _, item := range list {
		if item == s {
			return true
		}
	}
	return false
}

// unknownVariable reports an unknown VANDOX_ variable. The name is shown only if it is 1 to 64 bytes of
// [A-Za-z0-9_].
func unknownVariable(name string) *SecretError {
	if len(name) <= maxNameBytes && safeVariableName(name) {
		return &SecretError{Var: name, Reason: "unknown VANDOX_ variable"}
	}
	return &SecretError{Reason: "unknown VANDOX_ variable (name not shown: only 1 to 64 characters of [A-Za-z0-9_] are shown)"}
}

// safeVariableName reports whether name consists of [A-Za-z0-9_] only.
func safeVariableName(name string) bool {
	for i := 0; i < len(name); i++ {
		c := name[i]
		if (c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && c != '_' {
			return false
		}
	}
	return name != ""
}

// readSecret reads the secret called name from env, directly or through its _FILE variable. It returns
// the zero Secret when neither is set.
func readSecret(env map[string]string, name string) (Secret, error) {
	fileVar := name + FileSuffix
	direct, hasDirect := env[name]
	path, hasFile := env[fileVar]
	switch {
	case hasDirect && hasFile:
		return Secret{}, &SecretError{Var: name, Reason: name + " and " + fileVar + " are both set, set only one"}
	case hasFile:
		return readSecretFile(fileVar, path)
	case hasDirect:
		return secretFromValue(name, direct)
	}
	return Secret{}, nil
}

// secretFromValue checks v and returns it as a Secret. v is never shown.
func secretFromValue(variable, v string) (Secret, error) {
	switch {
	case v == "":
		return Secret{}, &SecretError{Var: variable, Reason: "is empty"}
	case len(v) > MaxSecretBytes:
		return Secret{}, &SecretError{Var: variable, Reason: "is longer than " + strconv.Itoa(MaxSecretBytes) + " bytes"}
	}
	for i := 0; i < len(v); i++ {
		if v[i] < 0x21 || v[i] > 0x7E {
			return Secret{}, &SecretError{Var: variable, Reason: "must consist of printable ASCII characters without spaces"}
		}
	}
	return newSecret(v), nil
}

// readSecretFile reads a secret from the file at path, which is the value of fileVar. Neither the path nor
// the content is shown in an error.
func readSecretFile(fileVar, path string) (Secret, error) {
	if !filepath.IsAbs(path) {
		return Secret{}, &SecretError{Var: fileVar, Reason: "must be an absolute path"}
	}
	info, err := os.Stat(path)
	if err != nil {
		return Secret{}, fileError(fileVar, "cannot read the file", err)
	}
	if !info.Mode().IsRegular() {
		return Secret{}, &SecretError{Var: fileVar, Reason: "must name a regular file"}
	}
	f, err := os.Open(path)
	if err != nil {
		return Secret{}, fileError(fileVar, "cannot read the file", err)
	}
	defer func() { _ = f.Close() }()
	data, err := io.ReadAll(io.LimitReader(f, MaxSecretBytes+3))
	if err != nil {
		return Secret{}, fileError(fileVar, "cannot read the file", err)
	}
	if len(data) > MaxSecretBytes+2 {
		return Secret{}, &SecretError{Var: fileVar, Reason: "file is larger than " + strconv.Itoa(MaxSecretBytes) + " bytes plus the line ending"}
	}
	return secretFromValue(fileVar, trimLineEnding(string(data)))
}

// trimLineEnding removes exactly one trailing line feed and, if that was removed, one carriage return before it.
func trimLineEnding(s string) string {
	rest, ok := strings.CutSuffix(s, "\n")
	if !ok {
		return s
	}
	return strings.TrimSuffix(rest, "\r")
}

// fileError builds the error for a failed file access. Only the errno of a *fs.PathError is kept, because
// the text of the PathError contains the path.
func fileError(fileVar, reason string, err error) *SecretError {
	var pe *fs.PathError
	if errors.As(err, &pe) {
		return &SecretError{Var: fileVar, Reason: reason, Err: pe.Err}
	}
	return &SecretError{Var: fileVar, Reason: reason}
}

// readAgentToken reads the agent token and checks its minimum length. It returns the zero Secret when the
// token is not set and required is false.
func readAgentToken(env map[string]string, required bool) (Secret, error) {
	token, err := readSecret(env, EnvAgentToken)
	if err != nil {
		return Secret{}, err
	}
	if !token.IsSet() {
		if required {
			return Secret{}, &SecretError{Var: EnvAgentToken, Reason: "is required (set it or " + EnvAgentToken + FileSuffix + ")"}
		}
		return Secret{}, nil
	}
	if len(token.Value()) < MinAgentTokenBytes {
		return Secret{}, &SecretError{Var: EnvAgentToken, Reason: "must be at least " + strconv.Itoa(MinAgentTokenBytes) + " characters"}
	}
	return token, nil
}
