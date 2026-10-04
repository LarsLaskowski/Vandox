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
)

const agentExample = "../../deploy/agent/agent.yaml"

// agentToken is a valid agent token of exactly MinAgentTokenBytes characters.
var agentToken = strings.Repeat("t", MinAgentTokenBytes)

func agentEnv() []string { return []string{tokenVar + "=" + agentToken} }

// agentLine is the line of every option in a document built by agentDoc.
var agentLine = map[string]int{"agent_id": 1, "backend.url": 3, "spool.directory": 5, "log.level": 7}

// agentDoc builds an agent file in a fixed layout. over maps an option key path to the raw YAML text of its
// value; options not in over get a valid value.
func agentDoc(over map[string]string) string {
	val := func(key, def string) string {
		if v, ok := over[key]; ok {
			return v
		}
		return def
	}
	return "agent_id: " + val("agent_id", "web-1") + "\n" +
		"backend:\n  url: " + val("backend.url", "http://100.64.0.1:8081") + "\n" +
		"spool:\n  directory: " + val("spool.directory", "/var/lib/vandox/spool") + "\n" +
		"log:\n  level: " + val("log.level", "info") + "\n"
}

const validAgent = "agent_id: web-1\nbackend:\n  url: http://100.64.0.1:8081\n"

// quote returns s as a single-quoted YAML scalar.
func quote(s string) string { return "'" + strings.ReplaceAll(s, "'", "''") + "'" }

// requireRedacted fails the test if v shows secret in any formatting, JSON or slog output.
func requireRedacted(t *testing.T, v any, secret string) {
	t.Helper()
	outputs := map[string]string{
		"%v":  fmt.Sprintf("%v", v),
		"%+v": fmt.Sprintf("%+v", v),
		"%#v": fmt.Sprintf("%#v", v),
	}
	raw, err := json.Marshal(v)
	if err != nil {
		t.Fatalf("json.Marshal(config) error = %v, want nil", err)
	}
	outputs["json"] = string(raw)
	var text, jsn bytes.Buffer
	slog.New(slog.NewTextHandler(&text, nil)).Info("m", slog.Any("cfg", v))
	slog.New(slog.NewJSONHandler(&jsn, nil)).Info("m", slog.Any("cfg", v))
	outputs["slog text"], outputs["slog json"] = text.String(), jsn.String()
	for name, got := range outputs {
		if strings.Contains(got, secret) {
			t.Errorf("%s output %q contains the secret, want it absent", name, got)
		}
		if !strings.Contains(got, redacted) {
			t.Errorf("%s output %q does not contain %q", name, got, redacted)
		}
	}
}

func TestDefaultAgent(t *testing.T) {
	got := DefaultAgent()
	if got.Spool.Directory != "/var/lib/vandox/spool" {
		t.Errorf("DefaultAgent().Spool.Directory = %q, want %q", got.Spool.Directory, "/var/lib/vandox/spool")
	}
	if got.Log.Level != "info" {
		t.Errorf("DefaultAgent().Log.Level = %q, want %q", got.Log.Level, "info")
	}
}

func TestAgentKeys(t *testing.T) {
	got := AgentKeys()
	want := []string{"agent_id", "backend.url", "spool.directory", "log.level"}
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Errorf("AgentKeys() = %v, want %v", got, want)
	}
}

func TestLoadAgent_Example(t *testing.T) {
	t.Run("loads with its values", func(t *testing.T) {
		got, err := LoadAgent(agentExample, agentEnv())
		if err != nil {
			t.Fatalf("LoadAgent(%q) error = %v, want nil", agentExample, err)
		}
		if got.AgentID != "web-1" {
			t.Errorf("AgentID = %q, want %q", got.AgentID, "web-1")
		}
		if got.Backend.URL != "http://100.64.0.1:8081" {
			t.Errorf("Backend.URL = %q, want %q", got.Backend.URL, "http://100.64.0.1:8081")
		}
		if got.Spool.Directory != "/var/lib/vandox/spool" {
			t.Errorf("Spool.Directory = %q, want %q", got.Spool.Directory, "/var/lib/vandox/spool")
		}
		if got.Log.Level != "info" {
			t.Errorf("Log.Level = %q, want %q", got.Log.Level, "info")
		}
		if got.Secrets.AgentToken.Value() != agentToken {
			t.Errorf("Secrets.AgentToken.Value() = %q, want %q", got.Secrets.AgentToken.Value(), agentToken)
		}
	})

	t.Run("sets every option explicitly and keeps the defaults", func(t *testing.T) {
		data, err := os.ReadFile(agentExample)
		if err != nil {
			t.Fatalf("os.ReadFile(%q) error = %v, want nil", agentExample, err)
		}
		probe := DefaultAgent()
		lines, err := decodeStrict(agentExample, data, &probe)
		if err != nil {
			t.Fatalf("decodeStrict(%q) error = %v, want nil", agentExample, err)
		}
		for _, key := range AgentKeys() {
			if lines[key] <= 0 {
				t.Errorf("example %q does not set %q explicitly (line %d)", agentExample, key, lines[key])
			}
		}
		got, err := LoadAgent(agentExample, agentEnv())
		if err != nil {
			t.Fatalf("LoadAgent(%q) error = %v, want nil", agentExample, err)
		}
		def := DefaultAgent()
		if got.Spool != def.Spool {
			t.Errorf("example Spool = %+v, want the default %+v", got.Spool, def.Spool)
		}
		if got.Log != def.Log {
			t.Errorf("example Log = %+v, want the default %+v", got.Log, def.Log)
		}
	})
}

func TestLoadAgent_Defaults(t *testing.T) {
	docs := map[string]string{
		"optional keys omitted":      validAgent,
		"empty section":              validAgent + "spool:\n",
		"section with null":          validAgent + "spool: ~\n",
		"children commented out":     validAgent + "log:\n  # level: debug\n",
		"empty section and comments": validAgent + "spool:\n  # directory: /x\nlog:\n",
	}
	for name, doc := range docs {
		t.Run(name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", doc, err)
			}
			def := DefaultAgent()
			if got.Spool != def.Spool || got.Log != def.Log {
				t.Errorf("LoadAgent(%q) Spool/Log = %+v/%+v, want %+v/%+v", doc, got.Spool, got.Log, def.Spool, def.Log)
			}
		})
	}
}

func TestLoadAgent_RequiredKeys(t *testing.T) {
	tests := []struct {
		name string
		doc  string
		key  string
	}{
		{"empty file", "", "agent_id"},
		{"only comments", "# nothing\n", "agent_id"},
		{"document marker only", "---\n", "agent_id"},
		{"null document", "~\n", "agent_id"},
		{"agent_id missing", "backend:\n  url: http://h:1\n", "agent_id"},
		{"backend.url missing", "agent_id: web-1\n", "backend.url"},
		{"backend section empty", "agent_id: web-1\nbackend:\n", "backend.url"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if got != nil {
				t.Errorf("LoadAgent(%q) = %+v, want nil on error", tt.doc, got)
			}
			requireKeyError(t, err, tt.key, 0)
		})
	}
}

func TestLoadAgent_UnknownKeys(t *testing.T) {
	tests := []struct {
		name string
		doc  string
		key  string
		line int
		hint bool
	}{
		{"top level typo", "agnet_id: web-1\n" + validAgent, "agnet_id", 1, false},
		{"typo in section", validAgent + "spool:\n  directry: /x\n", "spool.directry", 5, false},
		{"case-sensitive section", validAgent + "Log:\n  level: info\n", "Log", 4, false},
		{"case-sensitive key", validAgent + "log:\n  Level: info\n", "log.Level", 5, false},
		{"token", validAgent + "token: " + secretValue + "\n", "token", 4, true},
		{"agent token in backend section", "agent_id: web-1\nbackend:\n  url: http://h:1\n  agent_token: " + secretValue + "\n", "backend.agent_token", 4, true},
		{"upper-case password", validAgent + "PASSWORD: x\n", "PASSWORD", 4, true},
		{"secret in section", validAgent + "log:\n  my_secret: x\n", "log.my_secret", 5, true},
		{"backend secret key", "web:\n  password_hash: x\n" + validAgent, "web", 1, false},
		{"newline in key", validAgent + "\"a\\nb\": 1\n", "", 4, false},
		{"newline in key in section", validAgent + "spool:\n  \"a\\nb\": 1\n", "spool", 5, false},
		{"bidi in key", validAgent + "\"x\\u202Ey\": 1\n", "", 4, false},
		{"32 byte key", validAgent + sentinel + strings.Repeat("a", 24) + ": 1\n", "", 4, false},
		{"block scalar key", validAgent + "? |\n  " + sentinel + "\n: 1\n", "", 4, false},
		{"key with dot", validAgent + "\"a.b\": 1\n", "", 4, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			ke := requireKeyError(t, err, tt.key, tt.line)
			if got := strings.Contains(strings.ToLower(ke.Reason), "environment variable"); got != tt.hint {
				t.Errorf("Reason %q mentions environment variables = %v, want %v", ke.Reason, got, tt.hint)
			}
			requireNoLeak(t, err, sentinel, "\n", "\u202E")
		})
	}
}

func TestLoadAgent_InvalidValues(t *testing.T) {
	longID := strings.Repeat("a", 65)
	cases := map[string][]string{
		"agent_id": {"''", quote(longID), "-a", quote("a b"), quote("a/b"), quote(sentinel + "/value"), "~", "[a]", "{a: b}", "", quote("\u00E4")},
		"backend.url": {
			"''", "'ftp://h:1'", "'h:1'", "'http://'", quote("http://u:" + secretValue + "@h:1"),
			quote("http://h:1/?token=" + secretValue), "'http://h:1/?'", "'http://h:1/#f'", "'http://h:1/ingest'",
			"'http://h:0'", "'http://h:65536'", "'http://h:x'", "'http://h:+80'", "'http:opaque'",
			"'http://a%E2%80%A8b:1'", "'http://a%2Fb:1'", "'http://h_1:1'", quote("http://" + secretValue + " x:1"),
			"~", "[a]", "{a: b}", "'http://h:1/a'", "'http://h:123456'",
		},
		"spool.directory": {"''", "spool", "/var/lib/../x", "/var/lib/vandox/", quote(sentinel + "/x"), "~", "[a]", "{a: b}"},
		"log.level":       {"INFO", "trace", "''", quote(sentinel), "~", "[a]", "{a: b}"},
	}
	for key, values := range cases {
		for _, v := range values {
			t.Run(key+" "+truncate(v), func(t *testing.T) {
				doc := agentDoc(map[string]string{key: v})
				got, err := LoadAgent(writeTemp(t, doc), agentEnv())
				if got != nil {
					t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
				}
				requireKeyError(t, err, key, agentLine[key])
				requireNoLeak(t, err, sentinel)
			})
		}
	}
}

func TestLoadAgent_ControlCharacters(t *testing.T) {
	values := []string{
		`"a\x01b"`, `"a\u2028b"`, `"a\u2029b"`, `"a\u202Eb"`, `"\uFEFFa"`, `"S3NT1NEL\x01"`, `"a\tb"`,
	}
	for _, key := range []string{"log.level", "spool.directory"} {
		for _, v := range values {
			t.Run(key+" "+v, func(t *testing.T) {
				doc := agentDoc(map[string]string{key: v})
				_, err := LoadAgent(writeTemp(t, doc), agentEnv())
				requireKeyError(t, err, key, agentLine[key])
				requireNoLeak(t, err, sentinel, "\u202E", "\u2028", "\n")
			})
		}
	}
	t.Run("block scalar with a newline", func(t *testing.T) {
		doc := agentDoc(map[string]string{"log.level": "|\n  " + sentinel})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		requireKeyError(t, err, "log.level", agentLine["log.level"])
		requireNoLeak(t, err, sentinel)
	})
	t.Run("bidi in a path", func(t *testing.T) {
		doc := agentDoc(map[string]string{"spool.directory": `"/var/lib/\u202Ex"`})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		requireKeyError(t, err, "spool.directory", agentLine["spool.directory"])
	})
}

func TestLoadAgent_AcceptedValues(t *testing.T) {
	urls := []string{"http://100.64.0.1:8081", "https://nas.tailnet.ts.net", "http://[fd7a:115c:a1e0::1]:8081", "HTTP://h:1/", "http://h:65535", "http://h"}
	for _, u := range urls {
		t.Run("url "+u, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, agentDoc(map[string]string{"backend.url": quote(u)})), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(url %q) error = %v, want nil", u, err)
			}
			if got.Backend.URL != u {
				t.Errorf("LoadAgent(url %q).Backend.URL = %q, want %q", u, got.Backend.URL, u)
			}
		})
	}
	for _, id := range []string{"web-1", "a", strings.Repeat("a", 64), "A.b_c-1"} {
		t.Run("agent_id "+truncate(id), func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, agentDoc(map[string]string{"agent_id": id})), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(agent_id %q) error = %v, want nil", id, err)
			}
			if got.AgentID != id {
				t.Errorf("LoadAgent(agent_id %q).AgentID = %q, want %q", id, got.AgentID, id)
			}
		})
	}
	for _, lvl := range []string{"debug", "info", "warn", "error"} {
		t.Run("level "+lvl, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, agentDoc(map[string]string{"log.level": lvl})), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(level %q) error = %v, want nil", lvl, err)
			}
			if got.Log.Level != lvl {
				t.Errorf("LoadAgent(level %q).Log.Level = %q, want %q", lvl, got.Log.Level, lvl)
			}
		})
	}
	t.Run("directory", func(t *testing.T) {
		got, err := LoadAgent(writeTemp(t, agentDoc(map[string]string{"spool.directory": "/srv/spool"})), agentEnv())
		if err != nil {
			t.Fatalf("LoadAgent(directory) error = %v, want nil", err)
		}
		if got.Spool.Directory != "/srv/spool" {
			t.Errorf("LoadAgent(directory).Spool.Directory = %q, want %q", got.Spool.Directory, "/srv/spool")
		}
	})
}

func TestLoadAgent_UnsupportedYAML(t *testing.T) {
	tests := []struct {
		name string
		doc  string
		line int
	}{
		{"duplicate key", validAgent + "agent_id: other\n", 4},
		{"second document", validAgent + "---\nagent_id: x\n", 4},
		{"anchor", "agent_id: &a web-1\nbackend:\n  url: http://h:1\n", 1},
		{"top-level sequence", "- a\n", 1},
		{"custom tag", "agent_id: !env X\n", 1},
		{"syntax error", "agent_id: [\n", 1},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			requireKeyError(t, err, anyKey, tt.line)
		})
	}
}

func TestLoadAgent_FileErrors(t *testing.T) {
	t.Run("missing file", func(t *testing.T) {
		path := filepath.Join(t.TempDir(), "nope.yaml")
		_, err := LoadAgent(path, agentEnv())
		if err == nil || !errors.Is(err, fs.ErrNotExist) || !strings.Contains(err.Error(), path) {
			t.Errorf("LoadAgent(missing) error = %v, want fs.ErrNotExist naming %q", err, path)
		}
		var ke *KeyError
		if errors.As(err, &ke) {
			t.Errorf("LoadAgent(missing) error is a *KeyError, want a file error")
		}
	})
	for name, path := range map[string]string{"directory": t.TempDir(), "device file": os.DevNull} {
		t.Run(name, func(t *testing.T) {
			_, err := LoadAgent(path, agentEnv())
			if err == nil || !strings.Contains(err.Error(), path) {
				t.Fatalf("LoadAgent(%q) error = %v, want an error naming the path", path, err)
			}
			var ke *KeyError
			if errors.As(err, &ke) {
				t.Errorf("LoadAgent(%q) error is a *KeyError, want a file error", path)
			}
		})
	}
	t.Run("file larger than the limit", func(t *testing.T) {
		path := writeTemp(t, strings.Repeat("#\n", MaxFileBytes/2)+"#")
		_, err := LoadAgent(path, agentEnv())
		if err == nil || !strings.Contains(err.Error(), path) {
			t.Fatalf("LoadAgent(MaxFileBytes+1) error = %v, want an error naming the path", err)
		}
		var ke *KeyError
		if errors.As(err, &ke) {
			t.Errorf("LoadAgent(MaxFileBytes+1) error is a *KeyError, want a file error")
		}
	})
	t.Run("file of exactly the limit is read", func(t *testing.T) {
		path := writeTemp(t, strings.Repeat("#\n", MaxFileBytes/2))
		_, err := LoadAgent(path, agentEnv())
		requireKeyError(t, err, "agent_id", 0) // required key missing shows that the file was read
	})
}

func TestLoadAgent_Secrets(t *testing.T) {
	file := writeTemp(t, validAgent)
	tokenPath := filepath.Join(t.TempDir(), "token")
	if err := os.WriteFile(tokenPath, []byte(agentToken+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	t.Run("token from environment", func(t *testing.T) {
		got, err := LoadAgent(file, agentEnv())
		if err != nil {
			t.Fatalf("LoadAgent() error = %v, want nil", err)
		}
		if got.Secrets.AgentToken.Value() != agentToken {
			t.Errorf("LoadAgent() token = %q, want %q", got.Secrets.AgentToken.Value(), agentToken)
		}
	})
	t.Run("token from file", func(t *testing.T) {
		got, err := LoadAgent(file, []string{tokenFile + "=" + tokenPath})
		if err != nil {
			t.Fatalf("LoadAgent() error = %v, want nil", err)
		}
		if got.Secrets.AgentToken.Value() != agentToken {
			t.Errorf("LoadAgent() token = %q, want the token from the file", got.Secrets.AgentToken.Value())
		}
	})
	t.Run("token missing", func(t *testing.T) {
		_, err := LoadAgent(file, nil)
		requireSecretError(t, err, tokenVar)
	})
	t.Run("token of 31 characters", func(t *testing.T) {
		_, err := LoadAgent(file, []string{tokenVar + "=" + strings.Repeat("t", MinAgentTokenBytes-1)})
		requireSecretError(t, err, tokenVar)
	})
	t.Run("token of 32 characters", func(t *testing.T) {
		if _, err := LoadAgent(file, []string{tokenVar + "=" + strings.Repeat("t", MinAgentTokenBytes)}); err != nil {
			t.Errorf("LoadAgent(32 character token) error = %v, want nil", err)
		}
	})
	t.Run("both variable and file", func(t *testing.T) {
		_, err := LoadAgent(file, []string{tokenVar + "=" + agentToken, tokenFile + "=" + tokenPath})
		requireSecretError(t, err, anyKey)
	})
	t.Run("relative file path", func(t *testing.T) {
		_, err := LoadAgent(file, []string{tokenFile + "=" + secretValue})
		requireSecretError(t, err, tokenFile)
		requireNoLeak(t, err, sentinel)
	})
}

func TestLoadAgent_Environment(t *testing.T) {
	file := writeTemp(t, validAgent)
	rejected := []struct {
		name string
		env  string
		want string
	}{
		{"telegram token", "VANDOX_TELEGRAM_BOT_TOKEN=x", "VANDOX_TELEGRAM_BOT_TOKEN"},
		{"telegram token file", "VANDOX_TELEGRAM_BOT_TOKEN_FILE=/x", "VANDOX_TELEGRAM_BOT_TOKEN_FILE"},
		{"web password hash", "VANDOX_WEB_PASSWORD_HASH=x", "VANDOX_WEB_PASSWORD_HASH"},
		{"web password hash file", "VANDOX_WEB_PASSWORD_HASH_FILE=/x", "VANDOX_WEB_PASSWORD_HASH_FILE"},
		{"typo", "VANDOX_AGENT_TOKN=x", "VANDOX_AGENT_TOKN"},
		{"lower case", "vandox_agent_token=x", "vandox_agent_token"},
		{"unsafe newline", "VANDOX_A\nB=x", ""},
		{"unsafe bidi", "VANDOX_\u202EX=x", ""},
		{"unsafe hyphen", "VANDOX_A-B=x", ""},
		{"overlong", "VANDOX_" + strings.Repeat("A", 58) + "=x", ""},
	}
	for _, tt := range rejected {
		t.Run(tt.name, func(t *testing.T) {
			_, err := LoadAgent(file, append(agentEnv(), tt.env))
			requireSecretError(t, err, tt.want)
			requireNoLeak(t, err, "\n", "\u202E", "A-B")
		})
	}
	t.Run("same variable twice", func(t *testing.T) {
		_, err := LoadAgent(file, append(agentEnv(), tokenVar+"="+agentToken))
		requireSecretError(t, err, tokenVar)
	})
	t.Run("unrelated entries are ignored", func(t *testing.T) {
		if _, err := LoadAgent(file, append(agentEnv(), "PATH=/bin", "NOEQUALS", "VANDOXX=1")); err != nil {
			t.Errorf("LoadAgent(unrelated environment) error = %v, want nil", err)
		}
	})
}

func TestLoadAgent_Redaction(t *testing.T) {
	token := secretValue + strings.Repeat("x", MinAgentTokenBytes)
	got, err := LoadAgent(writeTemp(t, validAgent), []string{tokenVar + "=" + token})
	if err != nil {
		t.Fatalf("LoadAgent() error = %v, want nil", err)
	}
	if got.Secrets.AgentToken.Value() != token {
		t.Fatalf("Secrets.AgentToken.Value() = %q, want %q", got.Secrets.AgentToken.Value(), token)
	}
	requireRedacted(t, got, secretValue)
}

func TestLoadAgent_ErrorOrder(t *testing.T) {
	t.Run("first invalid value in document order", func(t *testing.T) {
		doc := agentDoc(map[string]string{"agent_id": "-a", "backend.url": "'ftp://h:1'", "log.level": "trace"})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		requireKeyError(t, err, "agent_id", 1)
	})
	t.Run("later invalid value when the first is valid", func(t *testing.T) {
		doc := agentDoc(map[string]string{"backend.url": "'ftp://h:1'", "log.level": "trace"})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		requireKeyError(t, err, "backend.url", 3)
	})
	t.Run("first structure error in document order", func(t *testing.T) {
		_, err := LoadAgent(writeTemp(t, "foo: 1\nagent_id: a\nagent_id: b\n"), agentEnv())
		requireKeyError(t, err, "foo", 1)
	})
	t.Run("file error before secret error", func(t *testing.T) {
		_, err := LoadAgent(writeTemp(t, agentDoc(map[string]string{"agent_id": "-a"})), []string{"VANDOX_AGENT_TOKN=x"})
		requireKeyError(t, err, "agent_id", 1)
	})
	t.Run("missing required key before missing token", func(t *testing.T) {
		_, err := LoadAgent(writeTemp(t, ""), nil)
		requireKeyError(t, err, "agent_id", 0)
	})
	t.Run("both error types are found with errors.As", func(t *testing.T) {
		_, err := LoadAgent(writeTemp(t, validAgent), nil)
		var se *SecretError
		var ke *KeyError
		if !errors.As(err, &se) || errors.As(err, &ke) {
			t.Errorf("LoadAgent(no token) error = %T (%v), want a *SecretError only", err, err)
		}
	})
}
