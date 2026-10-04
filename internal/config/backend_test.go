package config

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const backendExample = "../../deploy/backend/vandoxd.yaml"

const (
	webHashVar  = "VANDOX_WEB_PASSWORD_HASH"
	telegramVar = "VANDOX_TELEGRAM_BOT_TOKEN"
	phcHash     = "$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA"
	botToken    = "123456:ABC-def_GHI"
)

// backendLine is the line of every option in a document built by backendDoc.
var backendLine = map[string]int{"web.listen": 2, "ingest.listen": 4, "storage.directory": 6, "log.level": 8}

// backendDoc builds a backend file in a fixed layout. over maps an option key path to the raw YAML text of
// its value; the other options get a valid value.
func backendDoc(over map[string]string) string {
	val := func(key, def string) string {
		if v, ok := over[key]; ok {
			return v
		}
		return def
	}
	return "web:\n  listen: " + val("web.listen", ":9999") + "\n" +
		"ingest:\n  listen: " + val("ingest.listen", ":9998") + "\n" +
		"storage:\n  directory: " + val("storage.directory", "/data") + "\n" +
		"log:\n  level: " + val("log.level", "info") + "\n"
}

func TestDefaultBackend(t *testing.T) {
	got := DefaultBackend()
	if got.Web.Listen != ":8080" || got.Ingest.Listen != ":8081" || got.Storage.Directory != "/data" || got.Log.Level != "info" {
		t.Errorf("DefaultBackend() = %+v, want web :8080, ingest :8081, storage /data, log info", got)
	}
}

func TestBackendKeys(t *testing.T) {
	got := BackendKeys()
	want := []string{"web.listen", "ingest.listen", "storage.directory", "log.level"}
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Errorf("BackendKeys() = %v, want %v", got, want)
	}
}

func TestLoadBackend_Example(t *testing.T) {
	t.Run("loads with its values and without secrets", func(t *testing.T) {
		got, err := LoadBackend(backendExample, nil)
		if err != nil {
			t.Fatalf("LoadBackend(%q) error = %v, want nil", backendExample, err)
		}
		def := DefaultBackend()
		if got.Web != def.Web || got.Ingest != def.Ingest || got.Storage != def.Storage || got.Log != def.Log {
			t.Errorf("LoadBackend(%q) = %+v, want the example values %+v", backendExample, got, def)
		}
		if anySecretSet(got.Secrets) {
			t.Error("LoadBackend(example, nil) has a secret set, want none")
		}
	})

	t.Run("sets every option explicitly", requireBackendExampleSetsEveryKey)
}

// anySecretSet reports whether any of the three backend secrets is set.
func anySecretSet(s BackendSecrets) bool {
	return s.AgentToken.IsSet() || s.WebPasswordHash.IsSet() || s.TelegramBotToken.IsSet()
}

// requireBackendExampleSetsEveryKey checks that the backend example sets every option explicitly.
func requireBackendExampleSetsEveryKey(t *testing.T) {
	t.Helper()
	data, err := os.ReadFile(backendExample)
	if err != nil {
		t.Fatalf("os.ReadFile(%q) error = %v, want nil", backendExample, err)
	}
	probe := DefaultBackend()
	lines, err := decodeStrict(backendExample, data, &probe)
	if err != nil {
		t.Fatalf("decodeStrict(%q) error = %v, want nil", backendExample, err)
	}
	for _, key := range BackendKeys() {
		if lines[key] <= 0 {
			t.Errorf("example %q does not set %q explicitly (line %d)", backendExample, key, lines[key])
		}
	}
}

func TestLoadBackend_Defaults(t *testing.T) {
	tests := []struct {
		name  string
		doc   string
		level string
	}{
		{"empty file", "", "info"},
		{"only comments", "# nothing\n", "info"},
		{"document marker only", "---\n", "info"},
		{"null document", "~\n", "info"},
		{"empty section", "storage:\n", "info"},
		{"children commented out", "web:\n  # listen: :1\nlog:\n  # level: debug\n", "info"},
		{"one option set", "log:\n  level: debug\n", "debug"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadBackend(writeTemp(t, tt.doc), nil)
			if err != nil {
				t.Fatalf("LoadBackend(%q) error = %v, want nil", tt.doc, err)
			}
			want := DefaultBackend()
			want.Log.Level = tt.level
			if got.Web != want.Web || got.Ingest != want.Ingest || got.Storage != want.Storage || got.Log != want.Log {
				t.Errorf("LoadBackend(%q) = %+v, want %+v", tt.doc, got, want)
			}
		})
	}
}

func TestLoadBackend_UnknownKeys(t *testing.T) {
	tests := []struct {
		name string
		doc  string
		key  string
		line int
		hint bool
	}{
		{"top level typo", "webb:\n  listen: :1\n", "webb", 1, false},
		{"typo in section", "web:\n  listn: :1\n", "web.listn", 2, false},
		{"agent id is not a backend option", "agent_id: x\n", "agent_id", 1, false},
		{"case-sensitive", "Log:\n  level: info\n", "Log", 1, false},
		{"password hash in file", "web:\n  password_hash: " + secretValue + "\n", "web.password_hash", 2, true},
		{"agent token in file", "ingest:\n  agent_token: " + secretValue + "\n", "ingest.agent_token", 2, true},
		{"token top level", "token: " + secretValue + "\n", "token", 1, true},
		{"newline in key", "web:\n  \"a\\nb\": 1\n", "web", 2, false},
		{"bidi in key", "\"x\\u202Ey\": 1\n", "", 1, false},
		{"32 byte key", sentinel + strings.Repeat("a", 24) + ": 1\n", "", 1, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := LoadBackend(writeTemp(t, tt.doc), nil)
			ke := requireKeyError(t, err, tt.key, tt.line)
			if got := strings.Contains(strings.ToLower(ke.Reason), "environment variable"); got != tt.hint {
				t.Errorf("Reason %q mentions environment variables = %v, want %v", ke.Reason, got, tt.hint)
			}
			requireNoLeak(t, err, sentinel, "\n", "\u202E")
		})
	}
}

func TestLoadBackend_InvalidValues(t *testing.T) {
	listens := []string{
		"''", "'8080'", "':0'", "':65536'", "':http'", "':+80'", "'[::1]:+80'", "':-1'", "': 80'", "':808080'",
		"'localhost:8080'", "'[::1]:x'", quote(sentinel + ":80"), "~", "[a]", "{a: b}",
	}
	cases := map[string][]string{
		"web.listen":        listens,
		"ingest.listen":     listens,
		"storage.directory": {"''", "data", "/data/", "/a/../b", quote(sentinel), "~", "[a]", "{a: b}"},
		"log.level":         {"INFO", "trace", "''", quote(sentinel), "~", "[a]", "{a: b}", `"a\x01b"`, `"a\u2028b"`, `"a\u202Eb"`},
	}
	for key, values := range cases {
		for _, v := range values {
			t.Run(key+" "+truncate(v), func(t *testing.T) {
				doc := backendDoc(map[string]string{key: v})
				path := writeTemp(t, doc)
				got, err := LoadBackend(path, nil)
				if got != nil {
					t.Errorf("LoadBackend(%q) = %+v, want nil on error", doc, got)
				}
				_ = requireKeyError(t, err, key, backendLine[key])
				requireNoLeakBesidesFile(t, err, path, sentinel, "\u202E", "\u2028")
			})
		}
	}
	t.Run("storage directory with control characters", func(t *testing.T) {
		_, err := LoadBackend(writeTemp(t, backendDoc(map[string]string{"storage.directory": `"/data/\u202Ex"`})), nil)
		_ = requireKeyError(t, err, "storage.directory", backendLine["storage.directory"])
	})
}

func TestLoadBackend_ListenPorts(t *testing.T) {
	t.Run("same port on both listeners", func(t *testing.T) {
		doc := backendDoc(map[string]string{"web.listen": "':8080'", "ingest.listen": "'0.0.0.0:8080'"})
		_, err := LoadBackend(writeTemp(t, doc), nil)
		_ = requireKeyError(t, err, "ingest.listen", backendLine["ingest.listen"])
	})
	t.Run("same port with default of the other listener", func(t *testing.T) {
		_, err := LoadBackend(writeTemp(t, "web:\n  listen: ':8081'\n"), nil)
		_ = requireKeyError(t, err, "ingest.listen", 0)
	})
	t.Run("different ports on the same host", func(t *testing.T) {
		doc := backendDoc(map[string]string{"web.listen": "'0.0.0.0:8080'", "ingest.listen": "'0.0.0.0:8081'"})
		if _, err := LoadBackend(writeTemp(t, doc), nil); err != nil {
			t.Errorf("LoadBackend(different ports) error = %v, want nil", err)
		}
	})
	for _, v := range []string{":8080", "0.0.0.0:8080", "[::]:8081", "192.168.1.10:8080", "[::1]:80"} {
		for _, key := range []string{"web.listen", "ingest.listen"} {
			t.Run("accepted "+key+" "+v, func(t *testing.T) {
				got, err := LoadBackend(writeTemp(t, backendDoc(map[string]string{key: quote(v)})), nil)
				if err != nil {
					t.Fatalf("LoadBackend(%s %q) error = %v, want nil", key, v, err)
				}
				if have := listenOf(got, key); have != v {
					t.Errorf("LoadBackend(%s %q) = %q, want %q", key, v, have, v)
				}
			})
		}
	}
}

// listenOf returns the listen address of the web or ingest listener named by key.
func listenOf(cfg *Backend, key string) string {
	if key == "ingest.listen" {
		return cfg.Ingest.Listen
	}
	return cfg.Web.Listen
}

func TestLoadBackend_AcceptedValues(t *testing.T) {
	for _, lvl := range []string{"debug", "info", "warn", "error"} {
		t.Run("level "+lvl, func(t *testing.T) {
			got, err := LoadBackend(writeTemp(t, backendDoc(map[string]string{"log.level": lvl})), nil)
			if err != nil {
				t.Fatalf("LoadBackend(level %q) error = %v, want nil", lvl, err)
			}
			if got.Log.Level != lvl {
				t.Errorf("LoadBackend(level %q).Log.Level = %q, want %q", lvl, got.Log.Level, lvl)
			}
		})
	}
	t.Run("directory", func(t *testing.T) {
		got, err := LoadBackend(writeTemp(t, backendDoc(map[string]string{"storage.directory": "/srv/vandox"})), nil)
		if err != nil {
			t.Fatalf("LoadBackend(directory) error = %v, want nil", err)
		}
		if got.Storage.Directory != "/srv/vandox" {
			t.Errorf("LoadBackend(directory).Storage.Directory = %q, want %q", got.Storage.Directory, "/srv/vandox")
		}
	})
}

func TestLoadBackend_UnsupportedYAML(t *testing.T) {
	tests := []struct {
		name string
		doc  string
		line int
	}{
		{"duplicate key", "web:\n  listen: :1\n  listen: :2\n", 3},
		{"second document", "web:\n  listen: ':1'\n---\n", 3},
		{"alias", "web: &a\n  listen: ':1'\n", 1},
		{"syntax error", "web: [\n", 1},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := LoadBackend(writeTemp(t, tt.doc), nil)
			_ = requireKeyError(t, err, anyKey, tt.line)
		})
	}
}

func TestLoadBackend_FileErrors(t *testing.T) {
	missing := filepath.Join(t.TempDir(), "nope.yaml")
	if _, err := LoadBackend(missing, nil); err == nil || !strings.Contains(err.Error(), missing) {
		t.Errorf("LoadBackend(missing) error = %v, want an error naming %q", err, missing)
	}
	t.Run("file larger than the limit", func(t *testing.T) {
		path := writeTemp(t, strings.Repeat("#\n", MaxFileBytes/2)+"#")
		_, err := LoadBackend(path, nil)
		var ke *KeyError
		if err == nil || errors.As(err, &ke) || !strings.Contains(err.Error(), path) {
			t.Errorf("LoadBackend(MaxFileBytes+1) error = %v, want a file error naming the path", err)
		}
	})
	t.Run("file of exactly the limit loads", func(t *testing.T) {
		if _, err := LoadBackend(writeTemp(t, strings.Repeat("#\n", MaxFileBytes/2)), nil); err != nil {
			t.Errorf("LoadBackend(MaxFileBytes) error = %v, want nil", err)
		}
	})
}

func TestLoadBackend_Secrets(t *testing.T) {
	file := writeTemp(t, "")
	dir := t.TempDir()
	write := func(name, content string) string {
		p := filepath.Join(dir, name)
		if err := os.WriteFile(p, []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
		return p
	}
	t.Run("no secret is set", func(t *testing.T) {
		got, err := LoadBackend(file, nil)
		if err != nil {
			t.Fatalf("LoadBackend(no secrets) error = %v, want nil", err)
		}
		if anySecretSet(got.Secrets) {
			t.Errorf("LoadBackend(no secrets) secrets set, want IsSet false for all three")
		}
	})
	t.Run("all three from the environment", func(t *testing.T) {
		got, err := LoadBackend(file, []string{tokenVar + "=" + agentToken, webHashVar + "=" + phcHash, telegramVar + "=" + botToken})
		if err != nil {
			t.Fatalf("LoadBackend(all secrets) error = %v, want nil", err)
		}
		requireAllSecrets(t, got.Secrets, "LoadBackend(all secrets)")
	})
	t.Run("all three from files", func(t *testing.T) {
		got, err := LoadBackend(file, []string{
			tokenFile + "=" + write("a", agentToken+"\n"),
			webHashVar + "_FILE=" + write("w", phcHash+"\r\n"),
			telegramVar + "_FILE=" + write("t", botToken),
		})
		if err != nil {
			t.Fatalf("LoadBackend(secret files) error = %v, want nil", err)
		}
		requireAllSecrets(t, got.Secrets, "LoadBackend(secret files)")
	})
	t.Run("agent token of 31 characters", func(t *testing.T) {
		_, err := LoadBackend(file, []string{tokenVar + "=" + strings.Repeat("t", MinAgentTokenBytes-1)})
		_ = requireSecretError(t, err, tokenVar)
	})
	t.Run("agent token of 32 characters", func(t *testing.T) {
		if _, err := LoadBackend(file, []string{tokenVar + "=" + agentToken}); err != nil {
			t.Errorf("LoadBackend(32 character token) error = %v, want nil", err)
		}
	})
	t.Run("short telegram token is accepted", func(t *testing.T) {
		if _, err := LoadBackend(file, []string{telegramVar + "=" + botToken}); err != nil {
			t.Errorf("LoadBackend(short telegram token) error = %v, want nil", err)
		}
	})
	invalid := []struct {
		name string
		env  []string
		want string
	}{
		{"hash with space", []string{webHashVar + "=a b"}, webHashVar},
		{"empty hash", []string{webHashVar + "="}, webHashVar},
		{"both variable and file", []string{telegramVar + "=" + botToken, telegramVar + "_FILE=" + write("t2", botToken)}, anyKey},
		{"relative file", []string{webHashVar + "_FILE=rel/x"}, webHashVar + "_FILE"},
	}
	for _, tt := range invalid {
		t.Run(tt.name, func(t *testing.T) {
			_, err := LoadBackend(file, tt.env)
			_ = requireSecretError(t, err, tt.want)
		})
	}
}

// requireAllSecrets checks that all three backend secrets carry the test values.
func requireAllSecrets(t *testing.T, s BackendSecrets, label string) {
	t.Helper()
	if s.AgentToken.Value() != agentToken || s.WebPasswordHash.Value() != phcHash || s.TelegramBotToken.Value() != botToken {
		t.Errorf("%s = %q, %q, %q, want %q, %q, %q",
			label, s.AgentToken.Value(), s.WebPasswordHash.Value(), s.TelegramBotToken.Value(), agentToken, phcHash, botToken)
	}
}

func TestLoadBackend_Environment(t *testing.T) {
	file := writeTemp(t, "")
	rejected := []struct{ name, env, want string }{
		{"typo", "VANDOX_AGENT_TOKN=x", "VANDOX_AGENT_TOKN"},
		{"lower case", "vandox_agent_token=x", "vandox_agent_token"},
		{"lower case hash", "vandox_web_password_hash=x", "vandox_web_password_hash"},
		{"unsafe newline", "VANDOX_A\nB=x", ""},
		{"unsafe bidi", "VANDOX_\u202EX=x", ""},
		{"overlong", "VANDOX_" + strings.Repeat("A", 58) + "=x", ""},
	}
	for _, tt := range rejected {
		t.Run(tt.name, func(t *testing.T) {
			_, err := LoadBackend(file, []string{tt.env})
			_ = requireSecretError(t, err, tt.want)
			requireNoLeak(t, err, "\n", "\u202E")
		})
	}
	t.Run("same variable twice", func(t *testing.T) {
		_, err := LoadBackend(file, []string{webHashVar + "=a", webHashVar + "=b"})
		_ = requireSecretError(t, err, webHashVar)
	})
	t.Run("unrelated entries are ignored", func(t *testing.T) {
		if _, err := LoadBackend(file, []string{"PATH=/bin", "NOEQUALS", "VANDOXX=1"}); err != nil {
			t.Errorf("LoadBackend(unrelated environment) error = %v, want nil", err)
		}
	})
}

func TestLoadBackend_Redaction(t *testing.T) {
	agentSecret := secretValue + strings.Repeat("x", MinAgentTokenBytes)
	hash := "$argon2id$" + secretValue
	bot := "123:" + secretValue
	got, err := LoadBackend(writeTemp(t, ""), []string{tokenVar + "=" + agentSecret, webHashVar + "=" + hash, telegramVar + "=" + bot})
	if err != nil {
		t.Fatalf("LoadBackend() error = %v, want nil", err)
	}
	if got == nil {
		t.Fatal("LoadBackend() = nil, want a configuration")
	}
	requireRedacted(t, *got, secretValue)
	requireRedacted(t, got, secretValue)
}

func TestLoadBackend_ErrorOrder(t *testing.T) {
	t.Run("first invalid value in document order", func(t *testing.T) {
		doc := backendDoc(map[string]string{"web.listen": "'8080'", "log.level": "trace"})
		_, err := LoadBackend(writeTemp(t, doc), nil)
		_ = requireKeyError(t, err, "web.listen", 2)
	})
	t.Run("file error before secret error", func(t *testing.T) {
		doc := backendDoc(map[string]string{"log.level": "trace"})
		_, err := LoadBackend(writeTemp(t, doc), []string{"VANDOX_AGENT_TOKN=x"})
		_ = requireKeyError(t, err, "log.level", 8)
	})
	t.Run("secret error is not a key error", func(t *testing.T) {
		_, err := LoadBackend(writeTemp(t, ""), []string{"VANDOX_AGENT_TOKN=x"})
		var ke *KeyError
		if errors.As(err, &ke) {
			t.Errorf("LoadBackend(bad environment) error = %v, want a *SecretError, not a *KeyError", err)
		}
		_ = requireSecretError(t, err, "VANDOX_AGENT_TOKN")
	})
}
