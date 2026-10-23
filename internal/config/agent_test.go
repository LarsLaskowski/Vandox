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
	"slices"
	"strconv"
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
	if want := (Spool{Directory: "/var/lib/vandox/spool", MaxSize: "2GiB", MinFree: "1GiB", MaxBytes: 2 << 30, MinFreeBytes: 1 << 30}); got.Spool != want {
		t.Errorf("DefaultAgent().Spool = %+v, want %+v", got.Spool, want)
	}
	if got.Log.Level != "info" {
		t.Errorf("DefaultAgent().Log.Level = %q, want %q", got.Log.Level, "info")
	}
	if len(got.Processes.Programs) != 0 {
		t.Errorf("DefaultAgent().Processes.Programs = %+v, want an empty list", got.Processes.Programs)
	}
	if !slices.Equal(got.Services.Units, defaultServiceUnits) {
		t.Errorf("DefaultAgent().Services.Units = %q, want %q", got.Services.Units, defaultServiceUnits)
	}
	if want := (MariaDB{Enabled: true, Socket: DefaultMariaDBSocket}); got.MariaDB != want {
		t.Errorf("DefaultAgent().MariaDB = %+v, want %+v", got.MariaDB, want)
	}
}

func TestAgentKeys(t *testing.T) {
	got := AgentKeys()
	old := []string{"agent_id", "backend.url", "spool.directory", "spool.max_size", "spool.min_free", "log.level", "filesystem.mount_points", "processes.programs", "services.units", "mariadb.enabled", "mariadb.socket", "backups.processes"}
	shipping := []string{"log_shipping.journal", "log_shipping.min_priority", "log_shipping.files", "log_shipping.drop"}
	var rest []string
	for _, key := range got {
		if !slices.Contains(shipping, key) {
			rest = append(rest, key)
		}
	}
	if strings.Join(rest, ",") != strings.Join(old, ",") {
		t.Errorf("AgentKeys() without the log_shipping keys = %v, want the keys in their order %v", rest, old)
	}
	for _, key := range shipping {
		if n := strings.Count(","+strings.Join(got, ",")+",", ","+key+","); n != 1 {
			t.Errorf("AgentKeys() = %v holds %q %d times, want once", got, key, n)
		}
	}
	if len(got) != len(old)+len(shipping) {
		t.Errorf("AgentKeys() holds %d keys, want %d", len(got), len(old)+len(shipping))
	}
}

func TestLoadAgent_Example(t *testing.T) {
	t.Run("loads with its values", func(t *testing.T) {
		got, err := LoadAgent(agentExample, agentEnv())
		if err != nil {
			t.Fatalf("LoadAgent(%q) error = %v, want nil", agentExample, err)
		}
		requireExampleValues(t, got)
	})

	t.Run("sets every option explicitly and keeps the defaults", func(t *testing.T) {
		requireExampleSetsEveryKey(t)
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
		if !slices.Equal(got.Filesystem.MountPoints, def.Filesystem.MountPoints) {
			t.Errorf("example Filesystem.MountPoints = %q, want the default %q", got.Filesystem.MountPoints, def.Filesystem.MountPoints)
		}
		if len(got.Processes.Programs) != 0 {
			t.Errorf("example Processes.Programs = %+v, want the default empty list", got.Processes.Programs)
		}
		if got.MariaDB != def.MariaDB {
			t.Errorf("example MariaDB = %+v, want the default %+v", got.MariaDB, def.MariaDB)
		}
		requireExampleBackupsDefault(t, got, &def)
		requireExampleLogShippingDefault(t, got)
	})
}

// requireExampleLogShippingDefault checks that the example sets every log_shipping option at its default.
func requireExampleLogShippingDefault(t *testing.T, got *Agent) {
	t.Helper()
	ls := got.LogShipping
	if !ls.Journal || ls.MinPriority != "info" || len(ls.Files) != 0 || len(ls.Drop) != 0 {
		t.Errorf("example LogShipping = %+v, want the defaults: journal true, min_priority info, no files and no drop rules", ls)
	}
}

// requireExampleBackupsDefault checks that the example keeps the default of backups.processes.
func requireExampleBackupsDefault(t *testing.T, got, def *Agent) {
	t.Helper()
	if !slices.Equal(got.Backups.Processes, def.Backups.Processes) {
		t.Errorf("example Backups.Processes = %#v, want the default %#v", got.Backups.Processes, def.Backups.Processes)
	}
}

// requireExampleValues checks the values the agent example file declares.
func requireExampleValues(t *testing.T, got *Agent) {
	t.Helper()
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
	if !slices.Equal(got.Backups.Processes, wantDefaultBackupRules) {
		t.Errorf("Backups.Processes = %#v, want the default %#v", got.Backups.Processes, wantDefaultBackupRules)
	}
}

// requireExampleSetsEveryKey checks that the agent example sets every option explicitly.
func requireExampleSetsEveryKey(t *testing.T) {
	t.Helper()
	data, err := os.ReadFile(agentExample)
	if err != nil {
		t.Fatalf("os.ReadFile(%q) error = %v, want nil", agentExample, err)
	}
	probe := DefaultAgent()
	lines, err := decodeStrict(agentExample, data, &probe)
	if err != nil {
		t.Fatalf("decodeStrict(%q) error = %v, want nil", agentExample, err)
	}
	for _, key := range append(AgentKeys(), "log_shipping.journal", "log_shipping.min_priority", "log_shipping.files", "log_shipping.drop") {
		if lines[key] <= 0 {
			t.Errorf("example %q does not set %q explicitly (line %d)", agentExample, key, lines[key])
		}
	}
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
			_ = requireKeyError(t, err, tt.key, 0)
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
				path := writeTemp(t, doc)
				got, err := LoadAgent(path, agentEnv())
				if got != nil {
					t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
				}
				_ = requireKeyError(t, err, key, agentLine[key])
				requireNoLeakBesidesFile(t, err, path, sentinel)
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
				path := writeTemp(t, doc)
				_, err := LoadAgent(path, agentEnv())
				_ = requireKeyError(t, err, key, agentLine[key])
				requireNoLeakBesidesFile(t, err, path, sentinel, "\u202E", "\u2028", "\n")
			})
		}
	}
	t.Run("block scalar with a newline", func(t *testing.T) {
		doc := agentDoc(map[string]string{"log.level": "|\n    " + sentinel})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		_ = requireKeyError(t, err, "log.level", agentLine["log.level"])
		requireNoLeak(t, err, sentinel)
	})
	t.Run("bidi in a path", func(t *testing.T) {
		doc := agentDoc(map[string]string{"spool.directory": `"/var/lib/\u202Ex"`})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		_ = requireKeyError(t, err, "spool.directory", agentLine["spool.directory"])
	})
}

func TestLoadAgent_AcceptedValues(t *testing.T) {
	urls := []string{"http://100.64.0.1:8081", "https://nas.tailnet.ts.net", "http://[fd7a:115c:a1e0::1]:8081", "HTTP://h:1/", "http://h:65535", "http://h"}
	for _, u := range urls {
		t.Run("url "+u, func(t *testing.T) {
			got := loadAcceptedAgent(t, "url", "backend.url", quote(u))
			if got.Backend.URL != u {
				t.Errorf("LoadAgent(url %q).Backend.URL = %q, want %q", u, got.Backend.URL, u)
			}
		})
	}
	for _, id := range []string{"web-1", "a", strings.Repeat("a", 64), "A.b_c-1"} {
		t.Run("agent_id "+truncate(id), func(t *testing.T) {
			got := loadAcceptedAgent(t, "agent_id", "agent_id", id)
			if got.AgentID != id {
				t.Errorf("LoadAgent(agent_id %q).AgentID = %q, want %q", id, got.AgentID, id)
			}
		})
	}
	for _, lvl := range []string{"debug", "info", "warn", "error"} {
		t.Run("level "+lvl, func(t *testing.T) {
			got := loadAcceptedAgent(t, "level", "log.level", lvl)
			if got.Log.Level != lvl {
				t.Errorf("LoadAgent(level %q).Log.Level = %q, want %q", lvl, got.Log.Level, lvl)
			}
		})
	}
	t.Run("directory", func(t *testing.T) {
		got := loadAcceptedAgent(t, "directory", "spool.directory", "/srv/spool")
		if got.Spool.Directory != "/srv/spool" {
			t.Errorf("LoadAgent(directory).Spool.Directory = %q, want %q", got.Spool.Directory, "/srv/spool")
		}
	})
}

// loadAcceptedAgent loads a valid agent file with the one option key set to value
// and fails the test if it is rejected. what names the option in failure messages.
func loadAcceptedAgent(t *testing.T, what, key, value string) *Agent {
	t.Helper()
	got, err := LoadAgent(writeTemp(t, agentDoc(map[string]string{key: value})), agentEnv())
	if err != nil {
		t.Fatalf("LoadAgent(%s %q) error = %v, want nil", what, value, err)
	}
	return got
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
			_ = requireKeyError(t, err, anyKey, tt.line)
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
		requireNotKeyError(t, err, "LoadAgent(missing)")
	})
	for name, path := range map[string]string{"directory": t.TempDir(), "device file": os.DevNull} {
		t.Run(name, func(t *testing.T) {
			_, err := LoadAgent(path, agentEnv())
			requireFileErrorNaming(t, err, path, fmt.Sprintf("LoadAgent(%q)", path))
		})
	}
	t.Run("file larger than the limit", func(t *testing.T) {
		path := writeTemp(t, strings.Repeat("#\n", MaxFileBytes/2)+"#")
		_, err := LoadAgent(path, agentEnv())
		requireFileErrorNaming(t, err, path, "LoadAgent(MaxFileBytes+1)")
	})
	t.Run("file of exactly the limit is read", func(t *testing.T) {
		path := writeTemp(t, strings.Repeat("#\n", MaxFileBytes/2))
		_, err := LoadAgent(path, agentEnv())
		_ = requireKeyError(t, err, "agent_id", 0) // required key missing shows that the file was read
	})
}

// requireNotKeyError fails the test if err is a *KeyError instead of a file error.
func requireNotKeyError(t *testing.T, err error, label string) {
	t.Helper()
	var ke *KeyError
	if errors.As(err, &ke) {
		t.Errorf("%s error is a *KeyError, want a file error", label)
	}
}

// requireFileErrorNaming fails the test unless err is a non-key error naming path.
func requireFileErrorNaming(t *testing.T, err error, path, label string) {
	t.Helper()
	if err == nil || !strings.Contains(err.Error(), path) {
		t.Fatalf("%s error = %v, want an error naming the path", label, err)
	}
	requireNotKeyError(t, err, label)
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
		_ = requireSecretError(t, err, tokenVar)
	})
	t.Run("token of 31 characters", func(t *testing.T) {
		_, err := LoadAgent(file, []string{tokenVar + "=" + strings.Repeat("t", MinAgentTokenBytes-1)})
		_ = requireSecretError(t, err, tokenVar)
	})
	t.Run("token of 32 characters", func(t *testing.T) {
		if _, err := LoadAgent(file, []string{tokenVar + "=" + strings.Repeat("t", MinAgentTokenBytes)}); err != nil {
			t.Errorf("LoadAgent(32 character token) error = %v, want nil", err)
		}
	})
	t.Run("both variable and file", func(t *testing.T) {
		_, err := LoadAgent(file, []string{tokenVar + "=" + agentToken, tokenFile + "=" + tokenPath})
		_ = requireSecretError(t, err, anyKey)
	})
	t.Run("relative file path", func(t *testing.T) {
		_, err := LoadAgent(file, []string{tokenFile + "=" + secretValue})
		_ = requireSecretError(t, err, tokenFile)
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
			_ = requireSecretError(t, err, tt.want)
			requireNoLeak(t, err, "\n", "\u202E", "A-B")
		})
	}
	t.Run("same variable twice", func(t *testing.T) {
		_, err := LoadAgent(file, append(agentEnv(), tokenVar+"="+agentToken))
		_ = requireSecretError(t, err, tokenVar)
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
	requirePointerVerbRedacted(t, got, secretValue)
	requirePointerVerbRedacted(t, *got, secretValue)
}

func TestLoadAgent_ErrorOrder(t *testing.T) {
	t.Run("first invalid value in document order", func(t *testing.T) {
		doc := agentDoc(map[string]string{"agent_id": "-a", "backend.url": "'ftp://h:1'", "log.level": "trace"})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		_ = requireKeyError(t, err, "agent_id", 1)
	})
	t.Run("later invalid value when the first is valid", func(t *testing.T) {
		doc := agentDoc(map[string]string{"backend.url": "'ftp://h:1'", "log.level": "trace"})
		_, err := LoadAgent(writeTemp(t, doc), agentEnv())
		_ = requireKeyError(t, err, "backend.url", 3)
	})
	t.Run("first structure error in document order", func(t *testing.T) {
		_, err := LoadAgent(writeTemp(t, "foo: 1\nagent_id: a\nagent_id: b\n"), agentEnv())
		_ = requireKeyError(t, err, "foo", 1)
	})
	t.Run("file error before secret error", func(t *testing.T) {
		_, err := LoadAgent(writeTemp(t, agentDoc(map[string]string{"agent_id": "-a"})), []string{"VANDOX_AGENT_TOKN=x"})
		_ = requireKeyError(t, err, "agent_id", 1)
	})
	t.Run("missing required key before missing token", func(t *testing.T) {
		_, err := LoadAgent(writeTemp(t, ""), nil)
		_ = requireKeyError(t, err, "agent_id", 0)
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

// mountPointsReason is the reason of every rejected value of filesystem.mount_points.
const mountPointsReason = "must be a list of at most 16 absolute, clean paths of at most 1024 bytes each " +
	"(no trailing slash, no . or .. elements), each listed once"

// mountKey is the option key path of the mount points; mountKeyLine is its line in a document built by mountDoc.
const (
	mountKey     = "filesystem.mount_points"
	mountKeyLine = 5
)

// mountDoc builds an agent file whose key filesystem.mount_points (line mountKeyLine) is followed by value, the raw
// YAML text after the colon.
func mountDoc(value string) string {
	return validAgent + "filesystem:\n  mount_points:" + value + "\n"
}

// mountPaths returns n distinct paths /d00, /d01, ...
func mountPaths(n int) []string {
	paths := make([]string, 0, n)
	for i := 0; i < n; i++ {
		paths = append(paths, "/d"+strconv.Itoa(i/10)+strconv.Itoa(i%10))
	}
	return paths
}

// mountList returns n distinct paths as a block list below the key.
func mountList(n int) string {
	var b strings.Builder
	for _, p := range mountPaths(n) {
		b.WriteString("\n    - " + p)
	}
	return b.String()
}

// requireMountError loads doc and fails the test unless it is refused with exactly "config: <file>:<line>: <key>: <reason>".
func requireMountError(t *testing.T, doc string, line int, reason string) {
	t.Helper()
	path := writeTemp(t, doc)
	got, err := LoadAgent(path, agentEnv())
	if got != nil {
		t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
	}
	if err == nil {
		t.Fatalf("LoadAgent(%q) error = nil, want a *KeyError for %s at line %d", doc, mountKey, line)
	}
	want := "config: " + path + ":" + strconv.Itoa(line) + ": " + mountKey + ": " + reason
	if err.Error() != want {
		t.Errorf("LoadAgent(%q) error = %q, want %q", doc, err.Error(), want)
	}
}

func TestLoadAgent_MountPoints(t *testing.T) {
	long := "/" + strings.Repeat("a", 1023)
	tests := []struct {
		name string
		doc  string
		want []string
	}{
		{"absent takes the default", validAgent, []string{"/"}},
		{"section without the key takes the default", validAgent + "filesystem:\n", []string{"/"}},
		{"block list", mountDoc("\n    - /var\n    - /home"), []string{"/var", "/home"}},
		{"flow list", mountDoc(" [/, /var]"), []string{"/", "/var"}},
		{"quoted elements", mountDoc(` ["/var", '/home']`), []string{"/var", "/home"}},
		{"empty list means no mount point", mountDoc(" []"), []string{}},
		{"the list replaces the default", mountDoc(" [/var]"), []string{"/var"}},
		{"16 entries", mountDoc(mountList(16)), mountPaths(16)},
		{"entry of 1024 bytes", mountDoc(" [" + long + "]"), []string{long}},
		{"seq tag", mountDoc(" !!seq [/var]"), []string{"/var"}},
		{"str tag on an element", mountDoc("\n    - !!str /var"), []string{"/var"}},
		{"flow list over several lines", mountDoc(" [\n    /var,\n    /home\n  ]"), []string{"/var", "/home"}},
		{"comments between elements", mountDoc("\n    # first\n    - /var\n    # second\n    - /home"), []string{"/var", "/home"}},
		{"folded element", mountDoc("\n    - >-\n      /a b"), []string{"/a b"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", tt.doc, err)
			}
			if !slices.Equal(got.Filesystem.MountPoints, tt.want) || (len(tt.want) == 0 && got.Filesystem.MountPoints == nil) {
				t.Errorf("LoadAgent(%q) MountPoints = %#v, want %#v", tt.doc, got.Filesystem.MountPoints, tt.want)
			}
		})
	}
}

func TestLoadAgent_MountPointsRejected(t *testing.T) {
	long := "/" + strings.Repeat("a", 1024)
	rule := []struct {
		name  string
		value string
	}{
		{"17 entries", mountList(17)},
		{"entry of 1025 bytes", " [" + long + "]"},
		{"relative path", " [var]"},
		{"trailing slash", " [/var/]"},
		{"dot dot element", " [/var/../etc]"},
		{"dot element", " [/./var]"},
		{"double slash", " [//var]"},
		{"empty string", ` [""]`},
		{"empty string by tag", "\n    - !!str"},
		{"quoted tilde", "\n    - \"~\""},
		{"integer", "\n    - 1"},
		{"boolean", "\n    - true"},
		{"timestamp", "\n    - 2026-01-01"},
		{"hexadecimal", "\n    - 0x1F"},
		{"infinity", "\n    - .inf"},
		{"the same path twice", " [/var, /home, /var]"},
		{"the same path twice in a block list", "\n    - /var\n    - /var"},
		{"leak sentinel relative", " [" + sentinel + "]"},
		{"leak sentinel with dot dot", " [/" + sentinel + "/../x]"},
		{"leak sentinel twice", " [/" + sentinel + ", /" + sentinel + "]"},
	}
	for _, tt := range rule {
		t.Run("rule "+tt.name, func(t *testing.T) {
			requireMountError(t, mountDoc(tt.value), mountKeyLine, mountPointsReason)
		})
	}

	form := []struct {
		name   string
		value  string
		reason string
	}{
		{"scalar in place of the list", " /", "invalid value, want a list"},
		{"empty string in place of the list", ` ""`, "invalid value, want a list"},
		{"mapping in place of the list", " {a: b}", "invalid value, want a list"},
		{"nested list", " [[/a]]", "invalid value, want a list"},
		{"mapping element", " [{a: b}]", "invalid value, want a list"},
		{"null value of the key", "", "has no value"},
		{"tilde value of the key", " ~", "has no value"},
	}
	for _, tt := range form {
		t.Run("form "+tt.name, func(t *testing.T) {
			requireMountError(t, mountDoc(tt.value), mountKeyLine, tt.reason)
		})
	}

	// The YAML texts hold one backslash each: YAML decodes the escape, and the character rule then refuses the element.
	const charReason = "must not contain control, format or line-separating characters"
	chars := []struct {
		name  string
		value string
	}{
		{"nul escape", " [\"/a\\x00\"]"},
		{"right-to-left override escape", " [\"/a\\u202e\"]"},
		{"byte order mark escape", " [\"/a\\ufeff\"]"},
		{"tab escape", " [\"/a\\tb\"]"},
		{"literal block scalar", "\n    - |\n      /a"},
	}
	for _, tt := range chars {
		t.Run("character "+tt.name, func(t *testing.T) {
			if strings.HasPrefix(tt.value, " [") && strings.Count(tt.value, "\\") != 1 {
				t.Fatalf("test input %q holds %d backslashes, want exactly 1", tt.value, strings.Count(tt.value, "\\"))
			}
			requireMountError(t, mountDoc(tt.value), mountKeyLine, charReason)
		})
	}

	unsupported := []struct {
		name   string
		value  string
		line   int
		reason string
	}{
		{"anchor on an element", "\n    - &a /x", mountKeyLine + 1, "anchors and aliases are not supported"},
		{"alias element", "\n    - &a /x\n    - *a", mountKeyLine + 1, "anchors and aliases are not supported"},
		{"binary tag", "\n    - !!binary YQ==", mountKeyLine + 1, "unsupported tag"},
		{"set tag", " !!set {a}", mountKeyLine, "unsupported tag"},
	}
	for _, tt := range unsupported {
		t.Run("unsupported "+tt.name, func(t *testing.T) {
			requireMountError(t, mountDoc(tt.value), tt.line, tt.reason)
		})
	}
}

func TestLoadAgent_MountPointsNullElement(t *testing.T) {
	tests := []struct {
		name  string
		value string
		line  int
	}{
		{"tilde", "\n    - ~", mountKeyLine + 1},
		{"null tag", "\n    - !!null", mountKeyLine + 1},
		{"empty element", "\n    -", mountKeyLine + 1},
		{"null word after a path", "\n    - /a\n    - null", mountKeyLine + 2},
		{"tilde in a flow list", " [/a, ~, /b]", mountKeyLine},
		{"null before an empty string", ` [/a, null, ""]`, mountKeyLine},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			requireMountError(t, mountDoc(tt.value), tt.line, "has no value")
		})
	}
}

// programsKeyLine is the line of the key processes.programs in a document built by programsDoc.
const programsKeyLine = 5

// programsDoc builds an agent file whose key processes.programs (line programsKeyLine) is followed by value, the raw YAML text
// after the colon; the first element of a block list is on line 6.
func programsDoc(value string) string {
	return validAgent + "processes:\n  programs:" + value + "\n"
}

// programsErrorCase is a processes.programs value that is refused.
type programsErrorCase struct {
	name   string
	value  string
	line   int
	key    string
	reason string
}

// requireProgramsError loads the document and fails the test unless it is refused with exactly
// "config: <file>:<line>: <key>: <reason>".
func requireProgramsError(t *testing.T, tt programsErrorCase) {
	t.Helper()

	doc := programsDoc(tt.value)
	path := writeTemp(t, doc)
	got, err := LoadAgent(path, agentEnv())
	if got != nil {
		t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
	}
	if err == nil {
		t.Fatalf("LoadAgent(%q) error = nil, want an error for %s at line %d", doc, tt.key, tt.line)
	}
	want := "config: " + path + ":" + strconv.Itoa(tt.line) + ": " + tt.key + ": " + tt.reason
	if err.Error() != want {
		t.Errorf("LoadAgent(%q) error = %q, want %q", doc, err.Error(), want)
	}
	requireNoLeakBesidesFile(t, err, path, sentinel)
}

func TestLoadAgent_ProcessesPrograms(t *testing.T) {
	var many strings.Builder
	for i := 0; i < MaxProgramRules; i++ {
		many.WriteString("\n    - {program: p" + strconv.Itoa(i) + ", command: c" + strconv.Itoa(i) + "}")
	}
	tests := []struct {
		name string
		doc  string
		want []ProgramRule
	}{
		{"absent takes the default", validAgent, nil},
		{"section without the key takes the default", validAgent + "processes:\n", nil},
		{"empty list", programsDoc(" []"), nil},
		{"command rule", programsDoc("\n    - program: web\n      command: apache2"), []ProgramRule{{Program: "web", Command: "apache2"}}},
		{"prefix rule with the appended word", programsDoc("\n    - program: php-fpm\n      cmdline_prefix: \"php-fpm: pool \"\n      append_next_word: true"),
			[]ProgramRule{{Program: "php-fpm", CmdlinePrefix: "php-fpm: pool ", AppendNextWord: true}}},
		{"prefix rule with append_next_word false", programsDoc("\n    - {program: x, cmdline_prefix: abc, append_next_word: false}"),
			[]ProgramRule{{Program: "x", CmdlinePrefix: "abc"}}},
		{"rules in file order", programsDoc("\n    - {program: b, command: y}\n    - {program: a, cmdline_prefix: \"x \"}\n    - {program: c, command: z}"),
			[]ProgramRule{{Program: "b", Command: "y"}, {Program: "a", CmdlinePrefix: "x "}, {Program: "c", Command: "z"}}},
		{"program of the whole alphabet", programsDoc("\n    - {program: \"A0._:/@+-\", command: x}"), []ProgramRule{{Program: "A0._:/@+-", Command: "x"}}},
		{"program of 128 characters", programsDoc("\n    - {program: " + strings.Repeat("a", 128) + ", command: x}"), []ProgramRule{{Program: strings.Repeat("a", 128), Command: "x"}}},
		{"command of 256 bytes", programsDoc("\n    - {program: x, command: " + strings.Repeat("c", 256) + "}"), []ProgramRule{{Program: "x", Command: strings.Repeat("c", 256)}}},
		{"prefix of 256 bytes in two-byte characters", programsDoc("\n    - {program: x, cmdline_prefix: " + strings.Repeat("\u00e9", 128) + "}"),
			[]ProgramRule{{Program: "x", CmdlinePrefix: strings.Repeat("\u00e9", 128)}}},
		{"block rule list in a flow list", programsDoc(" [{program: x, command: y}]"), []ProgramRule{{Program: "x", Command: "y"}}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", tt.doc, err)
			}
			if !slices.Equal(got.Processes.Programs, tt.want) {
				t.Errorf("LoadAgent(%q) Programs = %#v, want %#v", tt.doc, got.Processes.Programs, tt.want)
			}
		})
	}

	t.Run("64 rules", func(t *testing.T) {
		doc := programsDoc(many.String())

		got, err := LoadAgent(writeTemp(t, doc), agentEnv())

		if err != nil || len(got.Processes.Programs) != MaxProgramRules {
			t.Errorf("LoadAgent(64 rules) = %d rules, %v, want %d rules and no error", len(got.Processes.Programs), err, MaxProgramRules)
		}
	})
}

func TestLoadAgent_ProcessesProgramsErrors(t *testing.T) {
	const (
		listReason    = "must be a list of at most 64 rules"
		programReason = "must be 1 to 128 characters of [A-Za-z0-9._:/@+-], starting with a letter or digit"
		oneOfReason   = "must set exactly one of command and cmdline_prefix"
		lengthReason  = "must be 1 to 256 bytes"
		charReason    = "must not contain control, format or line-separating characters"
		first         = "processes.programs[0]"
	)
	var many strings.Builder
	for i := 0; i <= MaxProgramRules; i++ {
		many.WriteString("\n    - {program: p" + strconv.Itoa(i) + ", command: c" + strconv.Itoa(i) + "}")
	}
	withProgram := func(program string) string { return "\n    - program: " + program + "\n      command: x" }
	tests := []programsErrorCase{
		{"65 rules", many.String(), programsKeyLine, "processes.programs", listReason},
		{"program empty", withProgram(`""`), 6, first + ".program", programReason},
		{"program starting with a hyphen", withProgram("-a"), 6, first + ".program", programReason},
		{"program starting with an underscore", withProgram("_a"), 6, first + ".program", programReason},
		{"program with a space", withProgram(`"a b"`), 6, first + ".program", programReason},
		{"program with a dollar sign", withProgram("a$b"), 6, first + ".program", programReason},
		{"program with a non-ASCII letter", withProgram("\u00e4"), 6, first + ".program", programReason},
		{"program of 129 characters", withProgram(strings.Repeat("a", 129)), 6, first + ".program", programReason},
		{"program that leaks", withProgram(sentinel + "$"), 6, first + ".program", programReason},
		{"neither command nor prefix", "\n    - program: x", 6, first, oneOfReason},
		{"command and prefix", "\n    - program: x\n      command: a\n      cmdline_prefix: b", 6, first, oneOfReason},
		{"error of the second rule", "\n    - {program: a, command: b}\n    - program: y", 7, "processes.programs[1]", oneOfReason},
		{"command of 257 bytes", "\n    - program: x\n      command: " + strings.Repeat("c", 257), 7, first + ".command", lengthReason},
		{"prefix of 257 bytes", "\n    - program: x\n      cmdline_prefix: " + strings.Repeat("c", 257), 7, first + ".cmdline_prefix", lengthReason},
		{"command of 129 two-byte characters", "\n    - program: x\n      command: " + strings.Repeat("\u00e9", 129), 7, first + ".command", lengthReason},
		{"command that leaks", "\n    - program: x\n      command: " + strings.Repeat(sentinel, 40), 7, first + ".command", lengthReason},
		{"append_next_word without a prefix", "\n    - program: x\n      command: y\n      append_next_word: true", 8, first + ".append_next_word", "needs cmdline_prefix"},
		{"null element", "\n    - ~", 6, first, "has no value"},
		{"empty element", "\n    -", 6, first, "has no value"},
		{"scalar element", "\n    - x", 6, first, "must be a section (a mapping of keys)"},
		{"sequence element", "\n    - [a]", 6, first, "must be a section (a mapping of keys)"},
		{"null element after a rule", "\n    - {program: a, command: b}\n    - ~", 7, "processes.programs[1]", "has no value"},
		{"unknown key in an element", "\n    - program: x\n      command: y\n      bogus: 1", 8, first + ".bogus", "unknown key"},
		{"duplicate key in an element", "\n    - program: x\n      command: y\n      program: z", 8, first + ".program", "duplicate key"},
		{"control character in the command", "\n    - program: x\n      command: \"a\\tb\"", 7, first + ".command", charReason},
		{"append_next_word not a boolean", "\n    - program: x\n      cmdline_prefix: y\n      append_next_word: maybe", 8, first + ".append_next_word", "invalid value, want true or false"},
		{"anchor on an element", "\n    - &a {program: x, command: y}", 6, first, "anchors and aliases are not supported"},
		{"anchor inside an element", "\n    - program: &a x\n      command: y", 6, first + ".program", "anchors and aliases are not supported"},
		{"custom tag inside an element", "\n    - program: !foo x\n      command: y", 6, first + ".program", "unsupported tag"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) { requireProgramsError(t, tt) })
	}
}

// servicesKeyLine is the line of the key services.units in a document built by servicesDoc.
const servicesKeyLine = 5

// servicesDoc builds an agent file whose key services.units (line servicesKeyLine) is followed by value, the raw YAML text
// after the colon; the first element of a block list is on line 6.
func servicesDoc(value string) string {
	return validAgent + "services:\n  units:" + value + "\n"
}

// servicesList returns the block list of entries, each one a single-quoted YAML scalar, for servicesDoc.
func servicesList(entries ...string) string {
	var b strings.Builder
	for _, e := range entries {
		b.WriteString("\n    - " + quote(e))
	}
	return b.String()
}

// defaultServiceUnits is the default of services.units: the eleven entries of the spec as full unit names.
var defaultServiceUnits = []string{
	"sw-cp-server.service", "sw-engine.service", "psa.service", "nginx.service", "apache2.service", "postfix.service",
	"dovecot.service", "mariadb.service", "plesk-php*-fpm.service", "php*-fpm.service", "fail2ban.service",
}

func TestLoadAgent_ServiceUnitsDefault(t *testing.T) {
	t.Run("DefaultServiceUnits", func(t *testing.T) {
		if got := DefaultServiceUnits(); !slices.Equal(got, defaultServiceUnits) {
			t.Errorf("DefaultServiceUnits() = %q, want %q", got, defaultServiceUnits)
		}
	})
	t.Run("DefaultAgent", func(t *testing.T) {
		if got := DefaultAgent().Services.Units; !slices.Equal(got, defaultServiceUnits) {
			t.Errorf("DefaultAgent().Services.Units = %q, want %q", got, defaultServiceUnits)
		}
	})
	docs := []struct {
		name string
		doc  string
	}{
		{"no services section", validAgent},
		{"section without the key", validAgent + "services:\n"},
	}
	for _, tt := range docs {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", tt.doc, err)
			}
			if !slices.Equal(got.Services.Units, defaultServiceUnits) {
				t.Errorf("LoadAgent(%q) Services.Units = %q, want the default %q", tt.doc, got.Services.Units, defaultServiceUnits)
			}
		})
	}
}

func TestLoadAgent_ServiceUnitsEmptyTurnsCollectorOff(t *testing.T) {
	docs := []struct {
		name string
		doc  string
	}{
		{"block form with an empty flow list", servicesDoc(" []")},
		{"flow form of the section", validAgent + "services: {units: []}\n"},
	}
	for _, tt := range docs {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", tt.doc, err)
			}
			if len(got.Services.Units) != 0 {
				t.Errorf("LoadAgent(%q) Services.Units = %q, want an empty list, not the default", tt.doc, got.Services.Units)
			}
		})
	}
}

func TestLoadAgent_ServiceUnitsOverrideAndSuffix(t *testing.T) {
	long := strings.Repeat("a", 247)
	many := make([]string, MaxServiceUnits)
	for i := range many {
		many[i] = "u" + strconv.Itoa(i)
	}
	wantMany := make([]string, MaxServiceUnits)
	for i := range many {
		wantMany[i] = many[i] + ".service"
	}
	tests := []struct {
		name string
		doc  string
		want []string
	}{
		{"the spec example replaces the default", servicesDoc(" [mariadb, nginx.service, \"plesk-php*-fpm\"]"),
			[]string{"mariadb.service", "nginx.service", "plesk-php*-fpm.service"}},
		{"block list in file order", servicesDoc(servicesList("zeta", "alpha", "mid")), []string{"zeta.service", "alpha.service", "mid.service"}},
		{"suffix service stays", servicesDoc(servicesList("nginx.service")), []string{"nginx.service"}},
		{"name without a suffix gets .service", servicesDoc(servicesList("nginx")), []string{"nginx.service"}},
		{"unknown suffix after a dot gets .service", servicesDoc(servicesList("foo.bar")), []string{"foo.bar.service"}},
		{"instance without a suffix", servicesDoc(servicesList("getty@tty1")), []string{"getty@tty1.service"}},
		{"instance with the suffix", servicesDoc(servicesList("user@1000.service")), []string{"user@1000.service"}},
		{"pattern characters", servicesDoc(servicesList("php?-fpm", "plesk-php*-fpm.service")), []string{"php?-fpm.service", "plesk-php*-fpm.service"}},
		{"whole alphabet", servicesDoc(servicesList("A0:_.@-z*?")), []string{"A0:_.@-z*?.service"}},
		{"255 bytes with the implied suffix", servicesDoc(servicesList(long)), []string{long + ".service"}},
		{"255 bytes with the suffix", servicesDoc(servicesList(long + ".service")), []string{long + ".service"}},
		{"64 entries", servicesDoc(servicesList(many...)), wantMany},
		{"same name on different types of suffix text", servicesDoc(servicesList("a", "a.b")), []string{"a.service", "a.b.service"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", tt.doc, err)
			}
			if !slices.Equal(got.Services.Units, tt.want) {
				t.Errorf("LoadAgent(%q) Services.Units = %q, want %q", tt.doc, got.Services.Units, tt.want)
			}
		})
	}
}

func TestLoadAgent_ServiceUnitsRefused(t *testing.T) {
	const (
		listReason     = "must be a list of at most 64 unit names"
		nameReason     = "must be a service unit name or pattern of 1 to 255 bytes of [A-Za-z0-9:_.@*?-], not starting with . or -"
		typeReason     = "must name a service unit"
		templateReason = "must name an instance, not a template"
		twiceReason    = "listed twice"
		typeKey        = "services.units"
		first          = "services.units[0]"
		second         = "services.units[1]"
	)
	tooMany := make([]string, MaxServiceUnits+1)
	for i := range tooMany {
		tooMany[i] = "u" + strconv.Itoa(i)
	}
	cases := []struct {
		name   string
		value  string
		line   int
		key    string
		reason string
	}{
		{"65 entries", servicesList(tooMany...), servicesKeyLine, typeKey, listReason},
		{"empty entry", servicesList(""), 6, first, nameReason},
		{"entry of 256 bytes with the implied suffix", servicesList(strings.Repeat("a", 248)), 6, first, nameReason},
		{"entry of 256 bytes with the suffix", servicesList(strings.Repeat("a", 248) + ".service"), 6, first, nameReason},
		{"entry with a space", servicesList("a b"), 6, first, nameReason},
		{"entry with a slash", servicesList("a/b"), 6, first, nameReason},
		{"entry with a dollar sign", servicesList("a$b"), 6, first, nameReason},
		{"entry with a non-ASCII letter", servicesList("ä"), 6, first, nameReason},
		{"entry with brackets", servicesList("php[78]-fpm"), 6, first, nameReason},
		{"entry starting with a dot", servicesList(".nginx"), 6, first, nameReason},
		{"entry starting with a hyphen", servicesList("-nginx"), 6, first, nameReason},
		{"entry with two at signs", servicesList("a@b@c"), 6, first, nameReason},
		{"entry that leaks", servicesList(sentinel + "$"), 6, first, nameReason},
		{"entry of 256 bytes that leaks", servicesList(sentinel + strings.Repeat("a", 241)), 6, first, nameReason},
		{"error of the second entry", servicesList("ok", "a b"), 7, second, nameReason},
		{"first error wins", servicesList("ok", "a b", ".c"), 7, second, nameReason},
		{"socket", servicesList("foo.socket"), 6, first, typeReason},
		{"timer", servicesList("foo.timer"), 6, first, typeReason},
		{"target", servicesList("foo.target"), 6, first, typeReason},
		{"mount", servicesList("foo.mount"), 6, first, typeReason},
		{"automount", servicesList("foo.automount"), 6, first, typeReason},
		{"swap", servicesList("foo.swap"), 6, first, typeReason},
		{"path", servicesList("foo.path"), 6, first, typeReason},
		{"slice", servicesList("foo.slice"), 6, first, typeReason},
		{"scope", servicesList("foo.scope"), 6, first, typeReason},
		{"device", servicesList("foo.device"), 6, first, typeReason},
		{"instance of another type", servicesList("foo@bar.socket"), 6, first, typeReason},
		{"template with the suffix", servicesList("foo@.service"), 6, first, templateReason},
		{"template without a suffix start", servicesList("@foo"), 6, first, templateReason},
		{"name and its full name", servicesList("nginx", "nginx.service"), 7, second, twiceReason},
		{"full name and its short name", servicesList("nginx.service", "nginx"), 7, second, twiceReason},
		{"same full name twice", servicesList("nginx.service", "nginx.service"), 7, second, twiceReason},
		{"same pattern twice", servicesList("php*-fpm", "mariadb", "php*-fpm.service"), 8, "services.units[2]", twiceReason},
		{"null element", "\n    - ~", 6, typeKey, "has no value"},
		{"empty element", "\n    -", 6, typeKey, "has no value"},
		{"null element after an entry", "\n    - nginx\n    - null", 7, typeKey, "has no value"},
		{"null element in a flow list", " [nginx, ~, mariadb]", servicesKeyLine, typeKey, "has no value"},
		{"null value of the key", "", servicesKeyLine, typeKey, "has no value"},
		{"scalar in place of the list", " nginx", servicesKeyLine, typeKey, "invalid value, want a list"},
		{"mapping in place of the list", " {a: b}", servicesKeyLine, typeKey, "invalid value, want a list"},
		{"mapping element", " [{a: b}]", servicesKeyLine, typeKey, "invalid value, want a list"},
		{"nested list", " [[nginx]]", servicesKeyLine, typeKey, "invalid value, want a list"},
	}
	for _, tt := range cases {
		t.Run(tt.name, func(t *testing.T) {
			doc := servicesDoc(tt.value)
			path := writeTemp(t, doc)

			got, err := LoadAgent(path, agentEnv())

			if got != nil {
				t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
			}
			if err == nil {
				t.Fatalf("LoadAgent(%q) error = nil, want an error for %s at line %d", doc, tt.key, tt.line)
			}
			want := "config: " + path + ":" + strconv.Itoa(tt.line) + ": " + tt.key + ": " + tt.reason
			if err.Error() != want {
				t.Errorf("LoadAgent(%q) error = %q, want %q", doc, err.Error(), want)
			}
			requireNoLeakBesidesFile(t, err, path, sentinel)
		})
	}
}

// mariadbDoc returns a valid agent file followed by the section mariadb with the raw YAML values given; an empty
// value leaves its line out. The section starts at line 4, enabled is line 5 and socket the next line.
func mariadbDoc(enabled, socket string) string {
	doc := validAgent + "mariadb:\n"
	if enabled != "" {
		doc += "  enabled: " + enabled + "\n"
	}
	if socket != "" {
		doc += "  socket: " + socket + "\n"
	}
	return doc
}

// badSocket is a refused value of mariadb.socket: its name and its raw YAML text.
type badSocket struct {
	name  string
	value string
}

// controlReasonText is the reason the decoder gives for a value with a control, format or line-separating character.
const controlReasonText = "must not contain control, format or line-separating characters"

// reason returns the reason the value is refused with: the decoder's for a control character, otherwise the rule's.
func (b badSocket) reason(rule string) string {
	if strings.ContainsAny(b.value, `\`) {
		return controlReasonText
	}
	return rule
}

// mariadbSocketReason is the rule's reason for a socket that is not an absolute, clean path.
const mariadbSocketReason = "must be an absolute, clean path of at most 107 bytes"

func TestLoadAgent_MariaDBConstants(t *testing.T) {
	if DefaultMariaDBSocket != "/run/mysqld/mysqld.sock" || MaxSocketPathBytes != 107 {
		t.Errorf("DefaultMariaDBSocket, MaxSocketPathBytes = %q, %d, want %q, 107", DefaultMariaDBSocket, MaxSocketPathBytes, "/run/mysqld/mysqld.sock")
	}
}

func TestLoadAgent_MariaDBAccepts(t *testing.T) {
	long107 := "/" + strings.Repeat("s", MaxSocketPathBytes-1)
	defaults := MariaDB{Enabled: true, Socket: DefaultMariaDBSocket}
	accepted := []struct {
		name string
		doc  string
		want MariaDB
	}{
		{"section omitted", validAgent, defaults},
		{"empty section", validAgent + "mariadb:\n", defaults},
		{"section with null", validAgent + "mariadb: ~\n", defaults},
		{"children commented out", validAgent + "mariadb:\n  # enabled: false\n", defaults},
		{"enabled false keeps the socket default", mariadbDoc("false", ""), MariaDB{Enabled: false, Socket: DefaultMariaDBSocket}},
		{"enabled true", mariadbDoc("true", ""), defaults},
		{"socket only keeps enabled", mariadbDoc("", "/var/run/mysqld/other.sock"), MariaDB{Enabled: true, Socket: "/var/run/mysqld/other.sock"}},
		{"both set", mariadbDoc("false", quote("/srv/db.sock")), MariaDB{Enabled: false, Socket: "/srv/db.sock"}},
		{"socket of 107 bytes", mariadbDoc("", long107), MariaDB{Enabled: true, Socket: long107}},
		{"root-level one-byte name", mariadbDoc("", "/s"), MariaDB{Enabled: true, Socket: "/s"}},
	}
	for _, tt := range accepted {
		t.Run("accepts "+tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", tt.doc, err)
			}
			if got.MariaDB != tt.want {
				t.Errorf("LoadAgent(%q).MariaDB = %+v, want %+v", tt.doc, got.MariaDB, tt.want)
			}
		})
	}
}

func TestLoadAgent_MariaDBRefusesSocket(t *testing.T) {
	long108 := "/" + strings.Repeat("s", MaxSocketPathBytes-1) + "s"
	badSockets := []badSocket{
		{"empty", "''"},
		{"relative", "mysqld.sock"},
		{"relative with a directory", "run/mysqld.sock"},
		{"dot segment", "/run/./mysqld.sock"},
		{"parent segment", "/run/../mysqld.sock"},
		{"double slash", "/run//mysqld.sock"},
		{"trailing slash", "/run/mysqld/"},
		{"root", "/"},
		{"108 bytes", long108},
		{"control character", `"/run/a\x01b.sock"`},
		{"newline", `"/run/a\nb.sock"`},
		{"tab", `"/run/a\tb.sock"`},
		{"format character", `"/run/a\u202Eb.sock"`},
		{"line separator", `"/run/a\u2028b.sock"`},
		{"byte order mark", `"/run/\uFEFFb.sock"`},
		{"leaks the value", quote(sentinel + "/not/absolute")},
		{"leaks a long value", "/" + sentinel + strings.Repeat("s", 120)},
	}
	for _, tt := range badSockets {
		t.Run("refuses socket "+tt.name, func(t *testing.T) {
			doc := mariadbDoc("", tt.value)
			path := writeTemp(t, doc)

			got, err := LoadAgent(path, agentEnv())

			if got != nil {
				t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
			}
			ke := requireKeyError(t, err, "mariadb.socket", 5)
			if want := tt.reason(mariadbSocketReason); ke.Reason != want {
				t.Errorf("KeyError.Reason = %q, want %q", ke.Reason, want)
			}
			requireNoLeakBesidesFile(t, err, path, sentinel, "\n", "\u202E", "\u2028", "\x01")
		})
	}

	for _, v := range []string{"~", "[a]", "{a: b}", "7"} {
		t.Run("refuses socket "+v, func(t *testing.T) {
			path := writeTemp(t, mariadbDoc("", v))

			got, err := LoadAgent(path, agentEnv())

			if got != nil {
				t.Errorf("LoadAgent(socket %s) = %+v, want nil on error", v, got)
			}
			_ = requireKeyError(t, err, "mariadb.socket", 5)
		})
	}
}

func TestLoadAgent_MariaDBRefusesEnabledAndUnknownKey(t *testing.T) {
	for _, v := range []string{"'true'", "'false'", "1", "0", "maybe", "~", "[true]", "{a: b}", quote(sentinel)} {
		t.Run("refuses enabled "+truncate(v), func(t *testing.T) {
			path := writeTemp(t, mariadbDoc(v, ""))

			got, err := LoadAgent(path, agentEnv())

			if got != nil {
				t.Errorf("LoadAgent(enabled %s) = %+v, want nil on error", v, got)
			}
			_ = requireKeyError(t, err, "mariadb.enabled", 5)
			requireNoLeakBesidesFile(t, err, path, sentinel)
		})
	}

	t.Run("refuses an unknown key of the section", func(t *testing.T) {
		path := writeTemp(t, validAgent+"mariadb:\n  port: 3306\n")

		_, err := LoadAgent(path, agentEnv())

		_ = requireKeyError(t, err, "mariadb.port", 5)
	})
}

// wantDefaultBackupRules is the default of backups.processes: one rule, command pzstd.
var wantDefaultBackupRules = []BackupRule{{Command: "pzstd"}}

// backupsKeyLine is the line of the key backups.processes in a document built by backupsDoc.
const backupsKeyLine = 5

// backupsDoc builds an agent file whose key backups.processes (line backupsKeyLine) is followed by value, the raw YAML text
// after the colon; the first element of a block list is on line 6.
func backupsDoc(value string) string {
	return validAgent + "backups:\n  processes:" + value + "\n"
}

func TestDefaultAgent_BackupProcesses(t *testing.T) {
	got := DefaultAgent().Backups.Processes

	if !slices.Equal(got, wantDefaultBackupRules) {
		t.Errorf("DefaultAgent().Backups.Processes = %#v, want exactly %#v", got, wantDefaultBackupRules)
	}
}

func TestLoadAgent_BackupProcesses(t *testing.T) {
	var many strings.Builder
	for i := 0; i < MaxProgramRules; i++ {
		many.WriteString("\n    - {command: c" + strconv.Itoa(i) + "}")
	}
	tests := []struct {
		name string
		doc  string
		want []BackupRule
	}{
		{"absent takes the default", validAgent, wantDefaultBackupRules},
		{"section without the key takes the default", validAgent + "backups:\n", wantDefaultBackupRules},
		{"null section takes the default", validAgent + "backups: ~\n", wantDefaultBackupRules},
		{"command rule replaces the default", backupsDoc("\n    - command: borg"), []BackupRule{{Command: "borg"}}},
		{"prefix rule", backupsDoc("\n    - cmdline_prefix: \"/usr/bin/restic \""), []BackupRule{{CmdlinePrefix: "/usr/bin/restic "}}},
		{"rules in file order", backupsDoc("\n    - {command: b}\n    - {cmdline_prefix: \"x \"}\n    - {command: pzstd}"),
			[]BackupRule{{Command: "b"}, {CmdlinePrefix: "x "}, {Command: "pzstd"}}},
		{"command of 256 bytes", backupsDoc("\n    - {command: " + strings.Repeat("c", 256) + "}"), []BackupRule{{Command: strings.Repeat("c", 256)}}},
		{"prefix of 256 bytes in two-byte characters", backupsDoc("\n    - {cmdline_prefix: " + strings.Repeat("\u00e9", 128) + "}"),
			[]BackupRule{{CmdlinePrefix: strings.Repeat("\u00e9", 128)}}},
		{"flow list", backupsDoc(" [{command: y}]"), []BackupRule{{Command: "y"}}},
		{"wildcard characters are literal text", backupsDoc("\n    - {command: \"pz*|(a\"}"), []BackupRule{{Command: "pz*|(a"}}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := LoadAgent(writeTemp(t, tt.doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", tt.doc, err)
			}
			if !slices.Equal(got.Backups.Processes, tt.want) {
				t.Errorf("LoadAgent(%q) Backups.Processes = %#v, want %#v", tt.doc, got.Backups.Processes, tt.want)
			}
		})
	}

	t.Run("64 rules", func(t *testing.T) {
		got, err := LoadAgent(writeTemp(t, backupsDoc(many.String())), agentEnv())

		if err != nil || len(got.Backups.Processes) != MaxProgramRules {
			t.Errorf("LoadAgent(64 rules) = %d rules, %v, want %d rules and no error", len(got.Backups.Processes), err, MaxProgramRules)
		}
	})
}

func TestLoadAgent_BackupProcessesEmptyListSwitchesTheCheckOff(t *testing.T) {
	withDefault, err := LoadAgent(writeTemp(t, validAgent), agentEnv())
	if err != nil {
		t.Fatalf("LoadAgent(default) error = %v, want nil", err)
	}
	if len(withDefault.Backups.Processes) != 1 {
		t.Fatalf("LoadAgent(default) Backups.Processes = %#v, want the one default rule", withDefault.Backups.Processes)
	}

	for _, value := range []string{" []", " [ ]"} {
		doc := backupsDoc(value)
		got, err := LoadAgent(writeTemp(t, doc), agentEnv())
		if err != nil {
			t.Fatalf("LoadAgent(%q) error = %v, want nil", doc, err)
		}
		if len(got.Backups.Processes) != 0 {
			t.Errorf("LoadAgent(%q) Backups.Processes = %#v, want no rule (the check is off, the default is not kept)", doc, got.Backups.Processes)
		}
	}
}

// requireBackupsError loads the document and fails the test unless it is refused with exactly
// "config: <file>:<line>: <key>: <reason>".
func requireBackupsError(t *testing.T, tt programsErrorCase) {
	t.Helper()

	doc := backupsDoc(tt.value)
	path := writeTemp(t, doc)
	got, err := LoadAgent(path, agentEnv())
	if got != nil {
		t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
	}
	if err == nil {
		t.Fatalf("LoadAgent(%q) error = nil, want an error for %s at line %d", doc, tt.key, tt.line)
	}
	want := "config: " + path + ":" + strconv.Itoa(tt.line) + ": " + tt.key + ": " + tt.reason
	if err.Error() != want {
		t.Errorf("LoadAgent(%q) error = %q, want %q", doc, err.Error(), want)
	}
	requireNoLeakBesidesFile(t, err, path, sentinel)
}

func TestLoadAgent_BackupProcessesErrors(t *testing.T) {
	const (
		listReason   = "must be a list of at most 64 rules"
		oneOfReason  = "must set exactly one of command and cmdline_prefix"
		lengthReason = "must be 1 to 256 bytes"
		charReason   = "must not contain control, format or line-separating characters"
		first        = "backups.processes[0]"
	)
	var many strings.Builder
	for i := 0; i <= MaxProgramRules; i++ {
		many.WriteString("\n    - {command: c" + strconv.Itoa(i) + "}")
	}
	tests := []programsErrorCase{
		{"65 rules", many.String(), backupsKeyLine, "backups.processes", listReason},
		{"neither command nor prefix", "\n    - {}", 6, first, oneOfReason},
		{"empty command", "\n    - command: \"\"", 6, first, oneOfReason},
		{"command and prefix", "\n    - command: a\n      cmdline_prefix: b", 6, first, oneOfReason},
		{"error of the second rule", "\n    - command: b\n    - cmdline_prefix: \"\"", 7, "backups.processes[1]", oneOfReason},
		{"command of 257 bytes", "\n    - command: " + strings.Repeat("c", 257), 6, first + ".command", lengthReason},
		{"prefix of 257 bytes", "\n    - cmdline_prefix: " + strings.Repeat("c", 257), 6, first + ".cmdline_prefix", lengthReason},
		{"command of 129 two-byte characters", "\n    - command: " + strings.Repeat("\u00e9", 129), 6, first + ".command", lengthReason},
		{"command that leaks", "\n    - command: " + strings.Repeat(sentinel, 40), 6, first + ".command", lengthReason},
		{"program key of the process rules", "\n    - program: x\n      command: y", 6, first + ".program", "unknown key"},
		{"append_next_word key of the process rules", "\n    - command: y\n      append_next_word: true", 7, first + ".append_next_word", "unknown key"},
		{"duplicate key", "\n    - command: y\n      command: z", 7, first + ".command", "duplicate key"},
		{"null element", "\n    - ~", 6, first, "has no value"},
		{"scalar element", "\n    - pzstd", 6, first, "must be a section (a mapping of keys)"},
		{"sequence element", "\n    - [pzstd]", 6, first, "must be a section (a mapping of keys)"},
		{"control character in the command", "\n    - command: \"a\\tb\"", 6, first + ".command", charReason},
		{"list given as text", " pzstd", backupsKeyLine, "backups.processes", "invalid value, want a list"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) { requireBackupsError(t, tt) })
	}
}

const (
	spoolMaxReason     = "must be a whole number of MiB or GiB from 64MiB to 64GiB, such as 512MiB or 2GiB"
	spoolMinFreeReason = "must be a whole number of MiB or GiB from 0MiB to 1024GiB, such as 0MiB or 1GiB"
	spoolMaxLine       = 5
	spoolMinFreeLine   = 6
)

// spoolDoc builds a valid agent file whose spool section sets max_size (line 5) and min_free (line 6) to the
// raw YAML texts given; an empty text leaves the key out.
func spoolDoc(maxSize, minFree string) string {
	doc := validAgent + "spool:\n"
	if maxSize != "" {
		doc += "  max_size: " + maxSize + "\n"
	}
	if minFree != "" {
		doc += "  min_free: " + minFree + "\n"
	}
	return doc
}

func TestLoadAgent_SpoolSizes(t *testing.T) {
	t.Run("defaults", testSpoolSizeDefaults)
	t.Run("valid values", testSpoolSizeValid)
	t.Run("max_size refused", func(t *testing.T) {
		testSpoolSizeRefused(t, "spool.max_size", spoolMaxLine, spoolMaxReason, maxSizeBad)
	})
	t.Run("min_free refused", func(t *testing.T) {
		testSpoolSizeRefused(t, "spool.min_free", spoolMinFreeLine, spoolMinFreeReason, minFreeBad)
	})
	t.Run("max_size is checked before min_free", testSpoolSizeOrder)
}

func testSpoolSizeDefaults(t *testing.T) {
	path := writeTemp(t, validAgent)
	got, err := LoadAgent(path, agentEnv())
	if err != nil {
		t.Fatalf("LoadAgent(%q) error = %v, want nil", validAgent, err)
	}
	want := Spool{Directory: "/var/lib/vandox/spool", MaxSize: "2GiB", MinFree: "1GiB", MaxBytes: 2 << 30, MinFreeBytes: 1 << 30}
	if got.Spool != want {
		t.Errorf("LoadAgent(%q).Spool = %+v, want %+v", validAgent, got.Spool, want)
	}
}

func testSpoolSizeValid(t *testing.T) {
	tests := []struct {
		name             string
		maxSize, minFree string
		wantMax, wantMin int64
	}{
		{"smallest cap and no reserve", "64MiB", "0MiB", 64 << 20, 0},
		{"largest cap and largest reserve", "64GiB", "1024GiB", 64 << 30, 1024 << 30},
		{"reserve of zero GiB", "2GiB", "0GiB", 2 << 30, 0},
		{"mebibytes", "512MiB", "1MiB", 512 << 20, 1 << 20},
		{"quoted", "'4GiB'", "\"256MiB\"", 4 << 30, 256 << 20},
		{"MiB value above one GiB", "65536MiB", "99999MiB", 64 << 30, 99999 << 20},
		{"only max_size set", "8GiB", "", 8 << 30, 1 << 30},
		{"only min_free set", "", "3GiB", 2 << 30, 3 << 30},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			doc := spoolDoc(tt.maxSize, tt.minFree)
			got, err := LoadAgent(writeTemp(t, doc), agentEnv())
			if err != nil {
				t.Fatalf("LoadAgent(%q) error = %v, want nil", doc, err)
			}
			if got.Spool.MaxBytes != tt.wantMax {
				t.Errorf("LoadAgent(%q).Spool.MaxBytes = %d, want %d", doc, got.Spool.MaxBytes, tt.wantMax)
			}
			if got.Spool.MinFreeBytes != tt.wantMin {
				t.Errorf("LoadAgent(%q).Spool.MinFreeBytes = %d, want %d", doc, got.Spool.MinFreeBytes, tt.wantMin)
			}
		})
	}
}

// maxSizeBad and minFreeBad are the raw YAML values the rule of the option refuses (spec E1).
var (
	maxSizeBad = []string{
		"1024", "'1 GiB'", "1.5GiB", "1GB", "-1GiB", "32MiB", "63MiB", "0MiB", "65GiB", "65537MiB", "2048GiB",
		"''", "1gib", "1GIB", "1MB", "1G", "GiB", "' 1GiB'", "'1GiB '", "+1GiB", "'1_0GiB'", "0x10GiB",
		"123456MiB", "99999999999999999999GiB", "'1GiB\\n'", "7" + sentinel + "GiB",
	}
	minFreeBad = []string{
		"1024", "'1 GiB'", "1.5GiB", "1GB", "-1GiB", "-0MiB", "2048GiB", "1025GiB", "1048577MiB",
		"''", "1gib", "1GIB", "1MB", "1G", "GiB", "' 1GiB'", "'1GiB '", "+1GiB", "'1_0GiB'",
		"123456MiB", "99999999999999999999GiB", "7" + sentinel + "GiB",
	}
)

func testSpoolSizeRefused(t *testing.T, key string, line int, reason string, values []string) {
	for _, value := range values {
		doc := spoolDoc(value, "1GiB")
		if key == "spool.min_free" {
			doc = spoolDoc("2GiB", value)
		}
		path := writeTemp(t, doc)
		got, err := LoadAgent(path, agentEnv())
		if got != nil {
			t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
		}
		if err == nil {
			t.Errorf("LoadAgent(%q) error = nil, want an error for %s", doc, key)
			continue
		}
		want := "config: " + path + ":" + strconv.Itoa(line) + ": " + key + ": " + reason
		if err.Error() != want {
			t.Errorf("LoadAgent(%q) error = %q, want %q", doc, err.Error(), want)
		}
		requireNoLeakBesidesFile(t, err, path, sentinel)
	}
}

func testSpoolSizeOrder(t *testing.T) {
	doc := spoolDoc("32MiB", "2048GiB")
	path := writeTemp(t, doc)
	_, err := LoadAgent(path, agentEnv())
	want := "config: " + path + ":5: spool.max_size: " + spoolMaxReason
	if err == nil || err.Error() != want {
		t.Errorf("LoadAgent(%q) error = %v, want %q", doc, err, want)
	}
}

func TestLoadAgent_SpoolSizeForms(t *testing.T) {
	tests := []struct {
		name   string
		key    string
		line   int
		value  string
		reason string
	}{
		{"max_size list", "spool.max_size", spoolMaxLine, "[1GiB]", "invalid value, want a string"},
		{"max_size block list", "spool.max_size", spoolMaxLine, "\n    - 1GiB", "invalid value, want a string"},
		{"max_size tilde", "spool.max_size", spoolMaxLine, "~", "has no value"},
		{"min_free list", "spool.min_free", spoolMinFreeLine, "[1GiB]", "invalid value, want a string"},
		{"min_free tilde", "spool.min_free", spoolMinFreeLine, "~", "has no value"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			doc := spoolDoc(tt.value, "1GiB")
			if tt.key == "spool.min_free" {
				doc = spoolDoc("2GiB", tt.value)
			}
			path := writeTemp(t, doc)
			got, err := LoadAgent(path, agentEnv())
			if got != nil {
				t.Errorf("LoadAgent(%q) = %+v, want nil on error", doc, got)
			}
			want := "config: " + path + ":" + strconv.Itoa(tt.line) + ": " + tt.key + ": " + tt.reason
			if err == nil || err.Error() != want {
				t.Errorf("LoadAgent(%q) error = %v, want %q", doc, err, want)
			}
		})
	}
}
