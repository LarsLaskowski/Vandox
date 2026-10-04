package config

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"go.yaml.in/yaml/v3"
)

const (
	secretValue = sentinel + "-value"
	tokenVar    = "VANDOX_AGENT_TOKEN"
	tokenFile   = "VANDOX_AGENT_TOKEN_FILE"
	redacted    = "[redacted]"
)

// secretFixture returns a Secret holding value. The value field is set directly because the package is
// tested white-box and the constructor is the loader.
func secretFixture(value string) Secret { return Secret{value: value} }

func TestSecret_Redaction(t *testing.T) {
	s := secretFixture(secretValue)

	t.Run("formatting verbs", func(t *testing.T) { requireFormattingRedacted(t, s) })
	t.Run("a struct holding a secret", func(t *testing.T) { requireHolderFormattingRedacted(t, s) })
	t.Run("pointer verb", func(t *testing.T) {
		requirePointerVerbRedacted(t, s, secretValue)
		requirePointerVerbRedacted(t, secretHolder{S: s}, secretValue)
		requirePointerVerbRedacted(t, AgentSecrets{AgentToken: s}, secretValue)
		requirePointerVerbRedacted(t, BackendSecrets{AgentToken: s}, secretValue)
	})
	t.Run("marshaling", func(t *testing.T) { requireMarshalingRedacted(t, s) })
	t.Run("slog handlers", func(t *testing.T) { requireSlogRedacted(t, s) })
	t.Run("value and IsSet", func(t *testing.T) { requireValueAndIsSet(t, s) })
}

// requirePointerVerbRedacted fails the test if %p of v shows secret. The verb is not valid for these types,
// so fmt prints the value through its reflection path unless the type hides it.
func requirePointerVerbRedacted(t *testing.T, v any, secret string) {
	t.Helper()
	verb := "%p"
	if got := fmt.Sprintf(verb, v); strings.Contains(got, secret) {
		t.Errorf("fmt.Sprintf(%q, %T) = %q, want the secret absent", verb, v, got)
	}
}

// secretHolder is a struct that holds a secret in an exported field.
type secretHolder struct{ S Secret }

func requireFormattingRedacted(t *testing.T, s Secret) {
	t.Helper()
	for _, verb := range []string{"%v", "%+v", "%#v", "%s", "%q", "%x", "%X", "%d"} {
		got := fmt.Sprintf(verb, s)
		if strings.Contains(got, secretValue) {
			t.Errorf("fmt.Sprintf(%q, secret) = %q, want the secret absent", verb, got)
		}
		if !strings.Contains(got, redacted) {
			t.Errorf("fmt.Sprintf(%q, secret) = %q, want it to contain %q", verb, got, redacted)
		}
	}
	if got := fmt.Sprint(s); got != redacted {
		t.Errorf("fmt.Sprint(secret) = %q, want %q", got, redacted)
	}
	if got := s.String(); got != redacted {
		t.Errorf("secret.String() = %q, want %q", got, redacted)
	}
}

func requireHolderFormattingRedacted(t *testing.T, s Secret) {
	t.Helper()
	for _, verb := range []string{"%v", "%+v", "%#v"} {
		got := fmt.Sprintf(verb, secretHolder{S: s})
		if strings.Contains(got, secretValue) || !strings.Contains(got, redacted) {
			t.Errorf("fmt.Sprintf(%q, holder) = %q, want %q and not the secret", verb, got, redacted)
		}
	}
}

func requireMarshalingRedacted(t *testing.T, s Secret) {
	t.Helper()
	gotJSON, err := json.Marshal(secretHolder{S: s})
	if err != nil {
		t.Fatalf("json.Marshal error = %v, want nil", err)
	}
	if want := `{"S":"[redacted]"}`; string(gotJSON) != want {
		t.Errorf("json.Marshal(holder) = %s, want %s", gotJSON, want)
	}
	gotYAML, err := yaml.Marshal(secretHolder{S: s})
	if err != nil {
		t.Fatalf("yaml.Marshal error = %v, want nil", err)
	}
	if strings.Contains(string(gotYAML), secretValue) || !strings.Contains(string(gotYAML), redacted) {
		t.Errorf("yaml.Marshal(holder) = %q, want %q and not the secret", gotYAML, redacted)
	}
	text, err := s.MarshalText()
	if err != nil || string(text) != redacted {
		t.Errorf("secret.MarshalText() = %q, %v, want %q, nil", text, err, redacted)
	}
}

func requireSlogRedacted(t *testing.T, s Secret) {
	t.Helper()
	handlers := map[string]func(*bytes.Buffer) slog.Handler{
		"text": func(b *bytes.Buffer) slog.Handler { return slog.NewTextHandler(b, nil) },
		"json": func(b *bytes.Buffer) slog.Handler { return slog.NewJSONHandler(b, nil) },
	}
	for name, mk := range handlers {
		var buf bytes.Buffer
		slog.New(mk(&buf)).Info("msg", slog.Any("s", s))
		got := buf.String()
		if strings.Contains(got, secretValue) || !strings.Contains(got, redacted) {
			t.Errorf("slog %s output = %q, want %q and not the secret", name, got, redacted)
		}
	}
	if got := s.LogValue().String(); got != redacted {
		t.Errorf("secret.LogValue() = %q, want %q", got, redacted)
	}
}

func requireValueAndIsSet(t *testing.T, s Secret) {
	t.Helper()
	if got := s.Value(); got != secretValue {
		t.Errorf("secret.Value() = %q, want %q", got, secretValue)
	}
	if !s.IsSet() {
		t.Error("secret.IsSet() = false, want true")
	}
	var zero Secret
	if zero.IsSet() {
		t.Error("Secret{}.IsSet() = true, want false")
	}
	if got := zero.Value(); got != "" {
		t.Errorf("Secret{}.Value() = %q, want empty", got)
	}
}

func TestSecretError_Error(t *testing.T) {
	tests := []struct {
		name string
		err  *SecretError
		want string
	}{
		{"var and reason", &SecretError{Var: tokenVar, Reason: "is empty"}, "config: VANDOX_AGENT_TOKEN: is empty"},
		{"with wrapped error", &SecretError{Var: tokenFile, Reason: "cannot read file", Err: fs.ErrNotExist}, "config: VANDOX_AGENT_TOKEN_FILE: cannot read file: " + fs.ErrNotExist.Error()},
		{"empty var", &SecretError{Reason: "unknown VANDOX_ variable"}, "config: unknown VANDOX_ variable"},
		{"empty var with wrapped error", &SecretError{Reason: "x", Err: fs.ErrExist}, "config: x: " + fs.ErrExist.Error()},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := tt.err.Error(); got != tt.want {
				t.Errorf("SecretError.Error() = %q, want %q", got, tt.want)
			}
		})
	}
	t.Run("unwrap returns the wrapped error", func(t *testing.T) {
		err := &SecretError{Var: tokenFile, Reason: "r", Err: fs.ErrNotExist}
		if !errors.Is(err, fs.ErrNotExist) {
			t.Error("errors.Is(err, fs.ErrNotExist) = false, want true")
		}
		if u := (&SecretError{Reason: "r"}).Unwrap(); u != nil {
			t.Errorf("Unwrap() = %v, want nil", u)
		}
	})
}

var agentKnown = []string{tokenVar, tokenFile}

func TestCheckEnviron_Accepted(t *testing.T) {
	tests := []struct {
		name    string
		environ []string
		want    map[string]string
	}{
		{"empty environ", nil, map[string]string{}},
		{"known variable", []string{tokenVar + "=abc"}, map[string]string{tokenVar: "abc"}},
		{"file variable", []string{tokenFile + "=/run/secrets/t"}, map[string]string{tokenFile: "/run/secrets/t"}},
		{"value with equals sign", []string{tokenVar + "=a=b"}, map[string]string{tokenVar: "a=b"}},
		{"present but empty", []string{tokenVar + "="}, map[string]string{tokenVar: ""}},
		{"entries without equals sign are ignored", []string{"NOEQUALS", "VANDOX_NOEQUALS", tokenVar + "=x"}, map[string]string{tokenVar: "x"}},
		{"other variables are ignored", []string{"PATH=/bin", "VANDOXX=1", "VANDOX=1", "HOME=/root"}, map[string]string{}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := checkEnviron(tt.environ, agentKnown)
			if err != nil {
				t.Fatalf("checkEnviron(%q) error = %v, want nil", tt.environ, err)
			}
			if len(got) != len(tt.want) {
				t.Errorf("checkEnviron(%q) = %v, want %v", tt.environ, got, tt.want)
			}
			for k, v := range tt.want {
				if g, ok := got[k]; !ok || g != v {
					t.Errorf("checkEnviron(%q)[%q] = %q (present %v), want %q", tt.environ, k, g, ok, v)
				}
			}
		})
	}
}

func TestCheckEnviron_Rejected(t *testing.T) {
	tests := []struct {
		name    string
		environ []string
		wantVar string
	}{
		{"typo", []string{"VANDOX_AGENT_TOKN=" + secretValue}, "VANDOX_AGENT_TOKN"},
		{"lower case", []string{"vandox_agent_token=" + secretValue}, "vandox_agent_token"},
		{"mixed case prefix", []string{"Vandox_Agent_Token=" + secretValue}, "Vandox_Agent_Token"},
		{"other binary's secret", []string{"VANDOX_TELEGRAM_BOT_TOKEN=" + secretValue}, "VANDOX_TELEGRAM_BOT_TOKEN"},
		{"other binary's secret file", []string{"VANDOX_WEB_PASSWORD_HASH_FILE=/x"}, "VANDOX_WEB_PASSWORD_HASH_FILE"},
		{"duplicate", []string{tokenVar + "=a", tokenVar + "=b"}, tokenVar},
		{"duplicate file variable", []string{tokenFile + "=/a", tokenFile + "=/b"}, tokenFile},
		{"unknown after known", []string{tokenVar + "=a", "VANDOX_X=" + secretValue}, "VANDOX_X"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := checkEnviron(tt.environ, agentKnown)
			_ = requireSecretError(t, err, tt.wantVar)
			if got != nil {
				t.Errorf("checkEnviron(%q) map = %v, want nil on error", tt.environ, got)
			}
			requireNoLeak(t, err, secretValue, sentinel)
		})
	}
	t.Run("duplicate says set more than once", func(t *testing.T) {
		_, err := checkEnviron([]string{tokenVar + "=a", tokenVar + "=b"}, agentKnown)
		if se := requireSecretError(t, err, tokenVar); !strings.Contains(se.Reason, "more than once") {
			t.Errorf("Reason = %q, want it to contain %q", se.Reason, "more than once")
		}
	})
}

func TestCheckEnviron_UnsafeNames(t *testing.T) {
	tests := []struct {
		name  string
		entry string
		shown string
	}{
		{"newline", "VANDOX_A\nB=x", "A\nB"},
		{"bidi override", "VANDOX_\u202EX=x", "\u202E"},
		{"hyphen", "VANDOX_A-B=x", "A-B"},
		{"non-ascii", "VANDOX_\u00E4=x", "\u00E4"},
		{"65 bytes", "VANDOX_" + strings.Repeat("A", 58) + "=x", strings.Repeat("A", 58)},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := checkEnviron([]string{tt.entry}, agentKnown)
			se := requireSecretError(t, err, "")
			if !strings.Contains(se.Reason, "name not shown") {
				t.Errorf("Reason = %q, want it to contain %q", se.Reason, "name not shown")
			}
			requireNoLeak(t, err, "\n", "\u202E", tt.shown)
		})
	}
	t.Run("64 bytes is shown", func(t *testing.T) {
		name := "VANDOX_" + strings.Repeat("A", 57)
		_, err := checkEnviron([]string{name + "=x"}, agentKnown)
		_ = requireSecretError(t, err, name)
	})
}

// secretEnv builds an environment map for readSecret.
func secretEnv(kv ...string) map[string]string {
	m := map[string]string{}
	for i := 0; i+1 < len(kv); i += 2 {
		m[kv[i]] = kv[i+1]
	}
	return m
}

func TestReadSecret_FromEnvironment(t *testing.T) {
	valid := []string{
		"a",
		"$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA",
		"123456:ABC-def_GHI",
		"!~",
		strings.Repeat("x", MaxSecretBytes),
	}
	for _, v := range valid {
		t.Run("valid "+truncate(v), func(t *testing.T) {
			requireSecretFromEnv(t, v)
		})
	}
	invalid := []string{
		"", "a b", "a\tb", "a\x01b", "a\nb", "a\r", "a\x7fb", "a\x00b", "\u00E4", " ", sentinel + " value",
		"\xEF\xBB\xBF" + sentinel, strings.Repeat("x", MaxSecretBytes+1),
	}
	for _, v := range invalid {
		t.Run("invalid "+truncate(v), func(t *testing.T) {
			got, err := readSecret(secretEnv(tokenVar, v), tokenVar)
			_ = requireSecretError(t, err, tokenVar)
			requireZeroSecret(t, got)
			requireNoLeak(t, err, sentinel)
		})
	}
	t.Run("unset secret", func(t *testing.T) {
		got, err := readSecret(secretEnv(), tokenVar)
		if err != nil || got.IsSet() {
			t.Errorf("readSecret(empty env) = set %v, %v, want unset and nil", got.IsSet(), err)
		}
	})
	t.Run("both variable and file", func(t *testing.T) {
		_, err := readSecret(secretEnv(tokenVar, "x", tokenFile, "/x"), tokenVar)
		_ = requireSecretError(t, err, anyKey)
		requireErrorNames(t, err, tokenVar, tokenFile)
	})
	t.Run("both present with empty values", func(t *testing.T) {
		_, err := readSecret(secretEnv(tokenVar, "", tokenFile, ""), tokenVar)
		_ = requireSecretError(t, err, anyKey)
	})
}

// requireSecretFromEnv checks that readSecret accepts v from the environment variable unchanged.
func requireSecretFromEnv(t *testing.T, v string) {
	t.Helper()
	got, err := readSecret(secretEnv(tokenVar, v), tokenVar)
	if err != nil {
		t.Fatalf("readSecret(%q) error = %v, want nil", truncate(v), err)
	}
	if got.Value() != v || !got.IsSet() {
		t.Errorf("readSecret(%q) = %q (set %v), want the value", truncate(v), got.Value(), got.IsSet())
	}
}

// requireZeroSecret fails the test if got is set although the read returned an error.
func requireZeroSecret(t *testing.T, got Secret) {
	t.Helper()
	if got.IsSet() {
		t.Error("readSecret returned a set secret together with an error, want the zero Secret")
	}
}

// requireErrorNames fails the test unless the message of err contains every name.
func requireErrorNames(t *testing.T, err error, names ...string) {
	t.Helper()
	for _, name := range names {
		if !strings.Contains(err.Error(), name) {
			t.Errorf("error %q, want it to name %q", err, name)
		}
	}
}

// truncate shortens long test values for subtest names.
func truncate(s string) string {
	if len(s) > 24 {
		return s[:24] + "..."
	}
	return s
}

func TestReadSecret_FromFile(t *testing.T) {
	accepted := []struct {
		name    string
		content string
		want    string
	}{
		{"plain", "tok", "tok"},
		{"line feed", "tok\n", "tok"},
		{"carriage return and line feed", "tok\r\n", "tok"},
		{"phc string", "$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA\n", "$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA"},
		{"maximum length with crlf", strings.Repeat("x", MaxSecretBytes) + "\r\n", strings.Repeat("x", MaxSecretBytes)},
	}
	for _, tt := range accepted {
		t.Run("accepted "+tt.name, func(t *testing.T) {
			path := writeTemp(t, tt.content)
			got, err := readSecret(secretEnv(tokenFile, path), tokenVar)
			if err != nil {
				t.Fatalf("readSecret(file %q) error = %v, want nil", truncate(tt.content), err)
			}
			if got.Value() != tt.want {
				t.Errorf("readSecret(file %q) = %q, want %q", truncate(tt.content), truncate(got.Value()), truncate(tt.want))
			}
		})
	}

	rejected := []struct {
		name    string
		content string
	}{
		{"two line feeds", "tok\n\n"},
		{"lone carriage return", "tok\r"},
		{"space before line feed", "tok \n"},
		{"empty file", ""},
		{"only a line feed", "\n"},
		{"space inside", sentinel + " value\n"},
		{"tab inside", sentinel + "\tvalue"},
		{"non-ascii", sentinel + "\u00E4"},
		{"bom", "\xEF\xBB\xBF" + sentinel + "\n"},
		{"too large", strings.Repeat("x", MaxSecretBytes+3)},
		{"value one too long with crlf", strings.Repeat("x", MaxSecretBytes+1) + "\r\n"},
	}
	for _, tt := range rejected {
		t.Run("rejected "+tt.name, func(t *testing.T) {
			path := writeTemp(t, tt.content)
			got, err := readSecret(secretEnv(tokenFile, path), tokenVar)
			_ = requireSecretError(t, err, anyKey)
			requireZeroSecret(t, got)
			requireNoLeak(t, err, sentinel, path)
		})
	}
}

func TestReadSecret_FileVariable(t *testing.T) {
	t.Run("relative or empty path is rejected without a file access", func(t *testing.T) {
		for _, v := range []string{"", "rel/x", secretValue, "./x"} {
			requireRelativePathRejected(t, v)
		}
	})

	t.Run("missing file", func(t *testing.T) {
		path := filepath.Join(t.TempDir(), secretValue)
		_, err := readSecret(secretEnv(tokenFile, path), tokenVar)
		_ = requireSecretError(t, err, tokenFile)
		requireNotExistWithoutPath(t, err)
		requireNoLeak(t, err, sentinel, path)
	})

	for name, path := range map[string]string{"directory": t.TempDir(), "device file": os.DevNull} {
		t.Run(name, func(t *testing.T) {
			_, err := readSecret(secretEnv(tokenFile, path), tokenVar)
			_ = requireSecretError(t, err, tokenFile)
			requireNoLeak(t, err, path)
		})
	}

	t.Run("symlink to a regular file is followed", func(t *testing.T) {
		link := symlinkToFile(t, "tok\n")
		got, err := readSecret(secretEnv(tokenFile, link), tokenVar)
		if err != nil || got.Value() != "tok" {
			t.Errorf("readSecret(symlink) = %q, %v, want %q, nil", got.Value(), err, "tok")
		}
	})
}

// requireRelativePathRejected checks that a relative or empty file path v is rejected as such.
func requireRelativePathRejected(t *testing.T, v string) {
	t.Helper()
	_, err := readSecret(secretEnv(tokenFile, v), tokenVar)
	se := requireSecretError(t, err, tokenFile)
	if !strings.Contains(se.Reason, "must be an absolute path") {
		t.Errorf("value %q: Reason = %q, want it to contain %q", v, se.Reason, "must be an absolute path")
	}
	if u := errors.Unwrap(err); u != nil {
		t.Errorf("value %q: errors.Unwrap(err) = %v, want nil", v, u)
	}
	requireNoLeak(t, err, sentinel, "rel/x")
}

// requireNotExistWithoutPath checks that err matches fs.ErrNotExist but does not keep the path.
func requireNotExistWithoutPath(t *testing.T, err error) {
	t.Helper()
	if !errors.Is(err, fs.ErrNotExist) {
		t.Errorf("errors.Is(err, fs.ErrNotExist) = false for %v, want true", err)
	}
	var pe *fs.PathError
	if errors.As(err, &pe) {
		t.Errorf("errors.As(err, *fs.PathError) = true for %v, want false (the path must not be kept)", err)
	}
}

// symlinkToFile writes content to a file and returns a symlink to it; it skips the test
// where symlinks are not available.
func symlinkToFile(t *testing.T, content string) string {
	t.Helper()
	dir := t.TempDir()
	target := filepath.Join(dir, "target")
	link := filepath.Join(dir, "link")
	if err := os.WriteFile(target, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, link); err != nil {
		t.Skipf("symlinks not available: %v", err)
	}
	return link
}
