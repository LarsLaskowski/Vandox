# Plan: Configuration loading for agent and backend

Source: Issue #11 | [spec.md](spec.md)
Status: Draft
Tier: security. The change adds a dependency (`go.yaml.in/yaml/v3`), and it implements two security areas
from `.squad/project.md`: area 8 (*Secrets handling*) and area 10 (*Parsing of external input*, which
names "configuration").

## Problem / root cause

Feature, see [spec.md](spec.md). Summary: a new shared package `internal/config` gives both binaries a
strict YAML configuration file plus secrets from the environment or `*_FILE` files, with commented example
files and tests that load them. The binaries are not wired to it in this change (#13 and #30 do that).

Claims of the issue, checked against the code and records:

- "Both binaries need the same robust configuration handling": **confirmed**. Neither has any. `cmd/*/main.go`
  only delegate to `internal/cli.Run`, which knows `-version` alone. `docs/ARCHITECTURE.md` (*Configuration*)
  says "Configuration loading is not implemented yet".
- "Secrets … must never live in the YAML file": **confirmed** as accepted record 0032. Its *Consequences*
  leave the enforcement to this feature. Record 0050 decides it.
- "`/etc/vandox/agent.yaml` for the agent; a mounted file for the backend": **consistent** with #42
  (`ReadWritePaths=/var/lib/vandox`, configuration installed by script) and #13 (compose file mounts the
  configuration file). The backend default path `/etc/vandox/vandoxd.yaml` is chosen here (0049).
- "Depends on #4, #5 (already merged)": **confirmed**. `internal/model` and `internal/wire` exist. The
  agent ID rule (`^[A-Za-z0-9][A-Za-z0-9._-]*$`, 1–64 bytes) lives unexported in `internal/wire/wire.go:72-76`
  and is checked inline in `Header.Validate` (`wire.go:97-99`). This plan exports it so the agent's
  configured ID can never be one the wire format rejects.
- "every option documented in commented example files under `deploy/agent/` and `deploy/backend/`":
  `deploy/agent/` holds only `.gitkeep`, and `deploy/backend/` holds only the `Dockerfile`. **Confirmed**:
  nothing exists yet.

Related findings on the way (no defect, but relevant):

- `yaml.v3`'s own error texts echo the offending value (verified: `` cannot unmarshal !!str `abc` into int ``).
  The loader must therefore never pass a value-decoding error text through (AC6).
- `yaml.v3` parses duplicate keys, several documents, aliases and custom tags (`!foo bar` → `"bar"`) into a
  `yaml.Node` without error (verified with v3.0.5). The strict decoder must reject each of them itself
  (AC7).
- `gopkg.in/yaml.v3` is archived. Its maintained continuation is `go.yaml.in/yaml/v3` (same code, module
  `go.yaml.in/yaml/v3`, v3.0.5, no requirements of its own) (0048).

## Acceptance criteria

All in package `internal/config` unless stated otherwise. "The value never appears" means that a test puts
a sentinel string such as `S3NT1NEL-value` into the offending place and asserts `err.Error()` does not
contain it.

- [ ] AC1 *Agent example loads*: `LoadAgent("../../deploy/agent/agent.yaml", env)` with
  `env = []string{"VANDOX_AGENT_TOKEN=<32+ printable chars>"}` returns no error. `AgentID`, `Backend.URL`,
  `Spool.Directory` and `Log.Level` equal the values written in the file.
- [ ] AC2 *Backend example loads*: `LoadBackend("../../deploy/backend/vandoxd.yaml", nil)` returns no error
  and the values written in the file.
- [ ] AC3 *Examples cover every option with its default*: every key path of the schema (`AgentKeys()` /
  `BackendKeys()`) is set explicitly in the example file, which is checked through the decoder's key-line
  map. For every key that has a default, the loaded value equals `DefaultAgent()` / `DefaultBackend()`.
  For the required agent keys (`agent_id`, `backend.url`) the example holds a valid placeholder.
- [ ] AC4 *Defaults*: a file that omits optional keys, holds an empty section (`spool:`) or a section with
  all children commented out, or is empty or only comments, yields the defaults:
  - agent: `spool.directory` = `/var/lib/vandox/spool`, `log.level` = `info`;
  - backend: `web.listen` = `:8080`, `ingest.listen` = `:8081`, `storage.directory` = `/data`,
    `log.level` = `info`.

  An empty agent file fails with a `*KeyError` `{Key: "agent_id", Line: 0}`. That is the first required
  key, and `backend.url` comes next once `agent_id` is set. An empty backend file loads.
- [ ] AC5 *Unknown keys*: an unknown key at the top level (`agnet_id`) and inside a section
  (`spool.directry`) fails with a `*KeyError` whose `Key` is the full dotted path (`agnet_id`,
  `spool.directry`) and whose `Line` is the key's line. Keys are case-sensitive (`Log.level` is unknown).
  A key whose last segment contains `token`, `password` or `secret` (in any letter case, e.g. `token`,
  `backend.agent_token`, `web.password_hash`, `telegram.bot_token`) is unknown too, and its `Reason`
  additionally says that secrets come only from environment variables. The value never appears.
- [ ] AC6 *Invalid values* fail with a `*KeyError` that names key and line. The value never appears. Table
  per key (accepted forms in *Approach → Value rules*):
  - `agent_id`: empty, 65 bytes, leading `-`, a space, a `/`.
  - `backend.url`: empty; `ftp://h:1`; `h:1` (no scheme); `http://` (no host); `http://u:p@h:1` (user
    info; the sentinel is the password); `http://h:1/?token=x` (query); `http://h:1/?` (empty query);
    `http://h:1/#f` (fragment); `http://h:1/ingest` (path other than empty or `/`); `http://h:0`;
    `http://h:65536`; `http://h:x`; `http:opaque`. Accepted: `http://100.64.0.1:8081`,
    `https://nas.tailnet.ts.net`, `HTTP://h:1/` (the scheme is case-insensitive).
  - `spool.directory`, `storage.directory`: empty, relative (`spool`), not clean (`/var/lib/../x`,
    `/var/lib/vandox/`).
  - `log.level` (both): `INFO`, `trace`, empty. Accepted: `debug`, `info`, `warn`, `error`.
  - `web.listen`, `ingest.listen`: `8080` (no colon), `:0`, `:65536`, `:http` (named port),
    `localhost:8080` (host name), `[::1]:x`. Accepted: `:8080`, `0.0.0.0:8080`, `[::]:8081`,
    `192.168.1.10:8080`.
  - `web.listen` and `ingest.listen` with the same port (`:8080` and `0.0.0.0:8080`) fail with a
    `*KeyError` on `ingest.listen`.
  - A value of the wrong YAML kind for a string option (a mapping, a sequence) and an explicit null
    (`agent_id:`, `agent_id: ~`) fail with a `*KeyError` on that key.
  - A string value containing a control character (`"a\x01b"`, a block scalar with a newline) fails.
- [ ] AC7 *Unsupported YAML constructs* fail with a `*KeyError` that carries the line (and the key where
  there is one): a duplicate key (top level and within a section, line of the second occurrence); a second
  YAML document (`---`); an anchor or alias (`&a`/`*a`); a merge key (`<<: *a` and `<<: {…}`); a
  non-string key (`? [a]`); a custom or unsupported tag (`!env X`, `!!binary aGk=`, `!foo {a: 1}`); a
  top-level sequence or scalar. A file that holds only `---` or `~` counts as empty (AC4).
- [ ] AC8 *File errors* return an error that contains the path and is not a `*KeyError`: missing file
  (`errors.Is(err, fs.ErrNotExist)`), a directory, `/dev/null` (not a regular file), a file of
  `MaxFileBytes+1` bytes, and a YAML syntax error (`a: [`). A file of exactly `MaxFileBytes` bytes made of
  comment lines loads (agent: then fails on `agent_id` required, which shows it was read).
- [ ] AC9 *Secret sources* (`VANDOX_AGENT_TOKEN` as the example, same function for all three):
  - value from `VANDOX_AGENT_TOKEN` → `Secrets.AgentToken.Value()` equals it;
  - value from `VANDOX_AGENT_TOKEN_FILE=<abs path>` → equals the file content; content `tok\n` and
    `tok\r\n` give `tok`. `tok\n\n`, `tok\r` and `tok \n` are rejected;
  - both set → `*SecretError` naming both variables;
  - `_FILE` set to an empty value, a relative path, a missing file, a directory, `/dev/null`, a file of
    `MaxSecretBytes+3` bytes, or an empty file → `*SecretError` naming `VANDOX_AGENT_TOKEN_FILE` and, where
    it is a path problem, the path. A file of `MaxSecretBytes` printable characters plus `\r\n` is accepted;
  - a value that is empty, contains a space, a tab, a control character, a non-ASCII character or a UTF-8
    BOM (`\xEF\xBB\xBF` prefix, also from a file) → `*SecretError`. A value of printable ASCII
    `0x21`–`0x7E` (e.g. a PHC string `$argon2id$v=19$m=65536,t=3,p=4$c2FsdA$aGFzaA`, a Telegram token
    `123456:ABC-def_GHI`) is accepted;
  - the secret value never appears in any of these errors (sentinel in the value or the file).
- [ ] AC10 *Required and optional secrets*: the agent without `VANDOX_AGENT_TOKEN`/`_FILE` fails with
  `*SecretError{Var: "VANDOX_AGENT_TOKEN"}`. An agent token of 31 characters fails on both binaries, and
  32 is accepted. The backend loads with no secret set, and `IsSet()` is then false for all three. The
  backend with all three set returns each value.
- [ ] AC11 *Environment strictness*: `VANDOX_TELEGRAM_BOT_TOKEN` or `VANDOX_WEB_PASSWORD_HASH` (and their
  `_FILE` forms) in the agent's environment, `VANDOX_AGENT_TOKN` (typo) and `vandox_agent_token`
  (lower case) on either binary each fail with a `*SecretError` naming the variable. The same known
  variable twice in `environ` fails. Entries without `=` and variables without the prefix (`PATH`,
  `VANDOXX`) are ignored.
- [ ] AC12 *Redaction*: for a `Secret` holding a sentinel, none of `fmt.Sprintf` with `%v %+v %#v %s %q %x
  %X %d`, `fmt.Sprint`, `fmt.Sprintf("%+v", agent)` / `("%#v", agent)` of a loaded `*Agent` and of a
  `Backend` value, `json.Marshal(agent)`, `slog` text and JSON handler output of `slog.Any("cfg", agent)`
  and of `slog.Any("s", secret)` contains the sentinel. Each contains `[redacted]`. `Value()` returns the
  sentinel. The zero `Secret` has `IsSet() == false`.
- [ ] AC13 *Error order and types*: when a file has two problems, the first in document order is reported.
  Structure and value errors come before secret and environment errors. `errors.As` finds `*KeyError` and
  `*SecretError`. `KeyError.Error()` has the form `config: <file>:<line>: <key>: <reason>` (`<file>:` alone
  when `Line` is 0, and no `<key>: ` when `Key` is empty). `SecretError.Error()` has the form
  `config: <var>: <reason>`.
- [ ] AC14 (`internal/wire`) *Shared agent ID rule*: `wire.ValidateAgentID` accepts `web-1`, `a`, 64 × `a`,
  `A.b_c-1`, and rejects ``, 65 × `a`, `-a`, `.a`, `a b`, `a/b`, `ä` with a `*model.FieldError{Field:
  "agent_id"}`. The existing `Header.Validate` tests keep passing unchanged.

## Approach

### Package layout (`internal/config`)

- `config.go`: the package doc, constants, `KeyError`, `readFile` (bounded read of a regular file) and the
  value checks shared by both binaries (directory, listen address, log level, string control characters).
- `decode.go`: the strict decoder `decodeStrict`, which parses with `yaml.v3` into a `yaml.Node` and then
  walks the node tree itself against the target struct (reflection over fields with an explicit `yaml` tag
  name). It never calls `Decode` on a whole document, so no `yaml.v3` value error text reaches the user.
  It returns the line of every key it set (dotted path → line), which the validation uses for its errors
  and AC3 uses for its coverage check.
- `secret.go`: `Secret`, `SecretError`, `readSecret`, and `checkEnviron` (the unknown `VANDOX_` check and
  the duplicate check).
- `agent.go` / `backend.go`: the option structs, `DefaultAgent`/`DefaultBackend`, `AgentKeys`/`BackendKeys`,
  `LoadAgent`/`LoadBackend` and their validation.

`Load*` order: `readFile` → `decodeStrict` into a copy of the defaults → validate the options in struct
field order → `checkEnviron` → read the secrets in the order listed in *Signatures*. The first error is
returned. Every function stays at or below gocognit 15. The walker is split into mapping, field and leaf
helpers, and no new gocognit exclusion is allowed (0047).

No `context.Context`: the loader reads one local regular file (checked with `os.Stat` *before* `open`, so a
FIFO cannot block it) and ≤ 3 small secret files at start-up. `os.ReadFile`-style reads cannot be
cancelled, so a context would be unused (0049).

### Accepted forms: the YAML file (guard: strict schema, no secrets in the file)

`yaml.v3` (v3.0.5) accepts the following. The decoder behaves as listed. Behavior was verified in a
scratch module where marked ✓.

| Input form | Behavior |
| ---------- | -------- |
| UTF-8, UTF-8 with BOM, UTF-16 LE/BE with BOM ✓ | Decoded by `yaml.v3`. The guard runs on the node tree, so it is encoding-independent. |
| Empty file, only comments ✓ (`Decode` → `io.EOF`) | Empty configuration, defaults apply (AC4). |
| Single document `---` / explicit null `~` ✓ (`!!null` scalar) | Empty configuration (AC4). |
| Second document after `---` ✓ (parsed without error) | `*KeyError` on the line of the second document (AC7). Checked by a second `Decode` that must return `io.EOF`. |
| Top level a sequence or non-null scalar | `*KeyError` "top level must be a mapping" (AC7). |
| Block and flow mappings (`a: {b: c}`), quoted keys (`"agent_id":`) | Same nodes, accepted. Quoted keys match by their value. |
| Key case variants (`Agent_ID`) | Unknown key (case-sensitive) (AC5). |
| Duplicate key ✓ (`yaml.Node` keeps both) | `*KeyError` on the second occurrence (AC7). |
| Non-scalar key (`? [a]`) ✓ / non-string scalar key (`1:`, `true:`) | Non-scalar key: `*KeyError` "key must be a string". A scalar key is matched by its text and is therefore unknown (AC5/AC7). |
| Merge key `<<` ✓ (tag `!!merge`) | Rejected as an unsupported construct, not merged (AC7). |
| Anchors `&a` and aliases `*a` ✓ (incl. self-referencing `&a [*a]`, which `yaml.v3` parses without error) | Any node with an `Anchor` or of kind `AliasNode` → `*KeyError` (AC7). There is no alias expansion, so no "billion laughs". |
| Tags: resolved or explicit core tags `!!str !!int !!bool !!float !!null !!timestamp !!map !!seq` | Accepted, and the leaf rules apply. |
| Other tags ✓ (`!foo bar` decodes to `"bar"`; `!!binary` base64-decodes; `!env X`) | `*KeyError` "unsupported tag" on any node (AC7). |
| Section key with null value (`spool:`, `spool: ~`) | Defaults of that section kept (AC4). |
| Leaf with null value (`agent_id:`) | `*KeyError` "has no value" (AC6). |
| Leaf given a mapping or sequence | `*KeyError` "want a string" (more generally: the description of the field's type) (AC6). |
| Leaf scalar not decodable into the field type (only reachable with non-string fields, covered by a white-box test struct in `decode_test.go` with `int`, `bool`, `time.Duration` and `[]string` fields) | `*KeyError` "invalid value, want <type description>". `yaml.v3`'s error text is discarded (AC6). |
| Plain scalars `5`, `true` for a string option | Taken as their text (`"5"`), then the option's own rule applies. |
| Block scalars (`|`, `>`) and quoted strings with escapes (`"a\x01b"`) ✓ | Decoded, then any control character (Unicode category Cc, which includes `\n`, `\t` and DEL) → `*KeyError` (AC6). |
| Nesting deeper than the schema | The leaf rule ("want a string") applies at the first level below a leaf. |
| File > `MaxFileBytes` (1 MiB), not a regular file, missing | `readFile` error with the path (AC8). Bounded read with `io.LimitReader(MaxFileBytes+1)`. |
| Syntax error | `fmt.Errorf("config: %s: %w", path, err)`, the `yaml.v3` scanner/parser message. That message carries a line and a description but no document text in the cases tested. The file holds no secrets by rule. |

Secret-carrying paths through the file, enumerated: the only keys that exist are `agent_id`, `backend.url`,
`spool.directory`, `log.level` (agent) and `web.listen`, `ingest.listen`, `storage.directory`, `log.level`
(backend). No secret field has a `yaml` tag name (secret fields carry `yaml:"-"`, and the walker only
matches explicit tag names, so no key can reach them). A credential embedded in a value is blocked where a
value could carry one: `backend.url` rejects user info and any query (AC6). The other values are bounded by
their own patterns (`agent_id`, listen address, log level), or they are paths.

### Value rules

- `agent_id` (required): `wire.ValidateAgentID`.
- `backend.url` (required): `url.Parse` succeeds; scheme `http` or `https` (url.Parse lower-cases it);
  `Opaque` empty; `User == nil`; `Host` non-empty with a non-empty `Hostname()`; the port, if present, is
  decimal 1–65535; `RawQuery == ""` and `!ForceQuery`; `Fragment == ""` and `RawFragment == ""`; `Path` is
  `""` or `"/"`. Plain `http` is allowed because the transport is the tailnet (0010, 0017). The URL is not
  checked to be a tailnet address, because MagicDNS names cannot be verified without resolving them. The
  Tailscale ACL stays the boundary (0049). `url.Parse`'s error text quotes the URL (it could hold user
  info), so it is discarded and only the rule is reported. The same goes for `net.SplitHostPort` and
  `strconv` errors in the other checks.
- `spool.directory`, `storage.directory`: `filepath.IsAbs` and `filepath.Clean(p) == p`.
- `log.level`: exactly one of `debug`, `info`, `warn`, `error`.
- `web.listen`, `ingest.listen`: `net.SplitHostPort` succeeds; host empty or `netip.ParseAddr` succeeds
  (zones allowed); port decimal 1–65535. The two ports must differ (error on `ingest.listen`).
- Every string leaf: no Unicode Cc character.

### Accepted forms: secrets from the environment (guard: secrets only from env/files)

Known variables per binary. Agent: `VANDOX_AGENT_TOKEN`, `VANDOX_AGENT_TOKEN_FILE`. Backend: those two
plus `VANDOX_WEB_PASSWORD_HASH`, `VANDOX_WEB_PASSWORD_HASH_FILE`, `VANDOX_TELEGRAM_BOT_TOKEN` and
`VANDOX_TELEGRAM_BOT_TOKEN_FILE`. The input is `environ []string` in `os.Environ()` form.

| Input form | Behavior |
| ---------- | -------- |
| Entry without `=` | Ignored (cannot be set through `os.Setenv`, may appear in a raw environ). |
| Name not starting with `VANDOX_` case-insensitively (`PATH`, `VANDOXX`) | Ignored. |
| Name starting with `VANDOX_` case-insensitively but not known to this binary (typo, lower case, the other binary's secret) | `*SecretError` naming the variable (AC11). |
| Known name twice | `*SecretError` "set more than once" (AC11). |
| `NAME` and `NAME_FILE` both present | `*SecretError` naming both (AC9). A present but empty value counts as present. |
| Neither present | Unset. The agent token is required on the agent (AC10). |
| `NAME=""` | `*SecretError` "is empty". |
| `NAME=v` | `v` must be 1–`MaxSecretBytes` bytes of printable ASCII `0x21`–`0x7E`. Anything else, including space, tab, CR/LF, NUL, DEL, non-ASCII and a BOM, → `*SecretError` with the reason only (AC9). |
| `NAME_FILE=""` / relative path | `*SecretError` "must be an absolute path". |
| `NAME_FILE=/abs` | `os.Stat` (follows symlinks, so the symlinked Docker/Kubernetes secrets work) must be a regular file. The file may hold at most `MaxSecretBytes+2` bytes (value plus `\r\n`), read through `io.LimitReader(MaxSecretBytes+3)`, and more is a "too large" error. Exactly one trailing `\n` is removed, then one trailing `\r` if that `\n` was removed. The rest must satisfy the value rule. The error wraps the `os` error (path included, content never) (AC9). |
| Agent token present on either binary | At least `MinAgentTokenBytes` (32) bytes (AC10). |

Error messages name the variable, the path and the rule, never the value or the file content.

### Redaction

`Secret` keeps its value in an unexported field and implements `fmt.Formatter` (every verb writes
`[redacted]`, so `%d` cannot reach the field by reflection), `fmt.Stringer`, `slog.LogValuer` and
`encoding.TextMarshaler` (so `encoding/json` and `yaml.v3` marshal `[redacted]`). `Value()` is the only
accessor (0050).

### Example files

`deploy/agent/agent.yaml` and `deploy/backend/vandoxd.yaml`. Every option is set explicitly with a comment
above it that gives its meaning, its rule and its default. Required agent options get valid placeholders
(`agent_id: web-1`, `backend.url: http://100.64.0.1:8081`). A header comment names the secret variables
and their `_FILE` forms and says that secrets never go into this file. `deploy/agent/.gitkeep` is
deleted. The `Dockerfile` and `.dockerignore` stay unchanged: the image does not ship the example.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `internal/config` (new) | `config.go`, `decode.go`, `secret.go`, `agent.go`, `backend.go` | New package as above |
| `internal/wire` | `wire.go` | New `ValidateAgentID`. `Header.Validate` calls it instead of the inline check (behavior unchanged). |
| module | `go.mod`, `go.sum` | `go get go.yaml.in/yaml/v3@v3.0.5`, then `go mod tidy` (direct requirement) |
| deploy | `deploy/agent/agent.yaml` (new), `deploy/backend/vandoxd.yaml` (new), `deploy/agent/.gitkeep` (deleted) | Commented example files |
| docs | `README.md`, `docs/ARCHITECTURE.md`, `.squad/project.md` | See *Documentation updates* |

`cmd/vandox-agent`, `cmd/vandoxd` and `internal/cli` are **not** changed.

## Signatures (for the Dev's skeleton)

```go
// Package config loads the configuration of vandox-agent and vandoxd: one strictly parsed YAML file per
// binary for the options, and the environment (directly or through *_FILE files) for the secrets.
package config

// File locations, limits and environment variables.
const (
	DefaultAgentFile   = "/etc/vandox/agent.yaml"
	DefaultBackendFile = "/etc/vandox/vandoxd.yaml"

	MaxFileBytes       = 1 << 20 // largest configuration file read
	MaxSecretBytes     = 4096    // largest secret value, also the largest *_FILE content (+ line ending)
	MinAgentTokenBytes = 32

	EnvAgentToken       = "VANDOX_AGENT_TOKEN"
	EnvWebPasswordHash  = "VANDOX_WEB_PASSWORD_HASH"
	EnvTelegramBotToken = "VANDOX_TELEGRAM_BOT_TOKEN"
	FileSuffix          = "_FILE"
)

// KeyError reports a problem in the configuration file, at a key or at a line.
type KeyError struct {
	File   string // path as given to the loader
	Line   int    // 1-based; 0 when the key is missing
	Key    string // dotted key path, e.g. "backend.url"; empty for document-level problems
	Reason string // never contains the value
}
func (e *KeyError) Error() string // "config: <File>:<Line>: <Key>: <Reason>", see AC13

// config.go, unexported
func readFile(path string, limit int64) ([]byte, error)
func checkDirectory(file string, lines map[string]int, key, value string) error
func checkListen(file string, lines map[string]int, key, value string) (port uint16, err error)
func checkLogLevel(file string, lines map[string]int, key, value string) error

// decode.go, unexported
// decodeStrict decodes the single YAML document in data into out (a non-nil pointer to a struct whose
// fields already hold the defaults) and returns the line of every key it set, by dotted path.
func decodeStrict(file string, data []byte, out any) (map[string]int, error)

// secret.go
type Secret struct{ /* value string */ }
func (s Secret) Value() string
func (s Secret) IsSet() bool
func (s Secret) String() string                      // "[redacted]"
func (s Secret) Format(f fmt.State, verb rune)       // "[redacted]" for every verb
func (s Secret) LogValue() slog.Value                // slog.StringValue("[redacted]")
func (s Secret) MarshalText() ([]byte, error)        // []byte("[redacted]"), nil

// SecretError reports a problem with a secret's environment variable or file.
type SecretError struct {
	Var    string // e.g. "VANDOX_AGENT_TOKEN_FILE"
	Reason string // never contains the value or the file content
	Err    error  // underlying error (e.g. from os.Stat), may be nil
}
func (e *SecretError) Error() string // "config: <Var>: <Reason>[: <Err>]"
func (e *SecretError) Unwrap() error

// unexported
func checkEnviron(environ []string, known []string) (map[string]string, error) // known: full names incl. _FILE
func readSecret(env map[string]string, name string) (Secret, error)

// agent.go
// Agent is the configuration of vandox-agent.
type Agent struct {
	AgentID string        `yaml:"agent_id"`
	Backend BackendTarget `yaml:"backend"`
	Spool   Spool         `yaml:"spool"`
	Log     Log           `yaml:"log"`
	Secrets AgentSecrets  `yaml:"-"`
}
type BackendTarget struct { URL string `yaml:"url"` }
type Spool struct { Directory string `yaml:"directory"` }
type Log struct { Level string `yaml:"level"` }
type AgentSecrets struct { AgentToken Secret }

func DefaultAgent() Agent
func AgentKeys() []string // every option key path in file order: agent_id, backend.url, spool.directory, log.level
func LoadAgent(path string, environ []string) (*Agent, error)

// backend.go
// Backend is the configuration of vandoxd.
type Backend struct {
	Web     Listener       `yaml:"web"`
	Ingest  Listener       `yaml:"ingest"`
	Storage Storage        `yaml:"storage"`
	Log     Log            `yaml:"log"`
	Secrets BackendSecrets `yaml:"-"`
}
type Listener struct { Listen string `yaml:"listen"` }
type Storage struct { Directory string `yaml:"directory"` }
type BackendSecrets struct {
	AgentToken       Secret // optional at load; #40 decides behavior when unset
	WebPasswordHash  Secret // optional at load; #25 refuses to start the UI without it
	TelegramBotToken Secret // optional at load; #60
}

func DefaultBackend() Backend
func BackendKeys() []string // web.listen, ingest.listen, storage.directory, log.level
func LoadBackend(path string, environ []string) (*Backend, error)
```

```go
// internal/wire/wire.go (new)
// ValidateAgentID reports whether id is a valid agent ID: 1 to 64 characters of [A-Za-z0-9._-],
// starting with a letter or digit. The error is a *model.FieldError for field "agent_id".
func ValidateAgentID(id string) error
```

Unexported helpers beyond those listed are the Dev's choice. The listed ones are what the Tester may call
from white-box tests. Skeleton bodies return zero values and `errors.New("not implemented")`. `Secret`'s
methods return `""`/zero. The skeleton adds `ValidateAgentID` as a stub only and does **not** yet change
`Header.Validate` (otherwise existing `wire` tests would break before step 5). That refactor is part of
step 6. The skeleton step also runs `go get go.yaml.in/yaml/v3@v3.0.5` and `go mod tidy` so the package
compiles with the import.

## Test files

- `internal/config/config_test.go`: `KeyError.Error` format (AC13), `readFile` (AC8), `checkDirectory`,
  `checkListen`, `checkLogLevel` tables (AC6).
- `internal/config/decode_test.go`: `decodeStrict` against a white-box test struct (string, int, bool,
  `time.Duration`, `[]string`, nested section, `yaml:"-"` field). Covers every row of the YAML
  accepted-forms table (AC5, AC6 leaf kinds, AC7) and the returned line map.
- `internal/config/secret_test.go`: `Secret` redaction (AC12 for the bare `Secret`), `SecretError` format,
  `checkEnviron` (AC11), `readSecret` (AC9).
- `internal/config/agent_test.go`: `LoadAgent`, including the example file (AC1, AC3), defaults (AC4),
  unknown keys and invalid values through the full loader (AC5, AC6), required/length rules (AC10),
  environment strictness (AC11), redaction of a loaded `*Agent` (AC12) and error order (AC13).
- `internal/config/backend_test.go`: the same for `LoadBackend` (AC2, AC3, AC4, AC6 listeners, AC10, AC11,
  AC12).
- `internal/wire/wire_test.go` (existing file, new test `TestValidateAgentID`): AC14.

Files are created with `t.TempDir()` and `os.WriteFile`. The examples are read via the relative path
`../../deploy/...` from the package directory. No `t.Setenv`: the environment is passed as `[]string`.

Existing test code that calls a changed signature: none. No existing signature changes, and
`Header.Validate` keeps its signature and behavior.

## Documentation updates (Dev, step 6)

- `README.md`: a new section *Configuration* with
  - the file locations (`/etc/vandox/agent.yaml`, `/etc/vandox/vandoxd.yaml` mounted into the container)
    and a pointer to the two example files;
  - one table per binary with the columns *Key*, *Environment variable*, *Default* and *Description* (per
    the integration surface). For every non-secret key the environment column is `—`;
  - a secrets table with *Variable*, *File variant*, *Binary*, *Required* and *Rule* (printable ASCII,
    agent token ≥ 32 characters, e.g. `openssl rand -hex 32`);
  - the strictness rules in two sentences (unknown keys/variables fail; errors name file, line and key).

  Also add `internal/config/` to the *Layout* block.
- `docs/ARCHITECTURE.md` *Configuration*: replace "Configuration loading is not implemented yet" with the
  implemented behavior (`internal/config`, strict file, secrets from env/`_FILE`, not yet called by the
  binaries until #13/#30) and link 0048–0050. Update the opening paragraph's list of what exists.
- `.squad/project.md`: in *Integration surface*, replace "(not implemented yet)" in the first bullet with
  `internal/config` (`Agent`/`Backend`, `LoadAgent`/`LoadBackend`, `AgentKeys`/`BackendKeys`). Name the
  example files `deploy/agent/agent.yaml` and `deploy/backend/vandoxd.yaml`, and the pinning tests
  `internal/config/agent_test.go` and `internal/config/backend_test.go`. Add a bullet "a new secret: its
  `Env*` constant, the known-variable list of each binary that reads it, the README secrets table". In
  *Security areas* 8 and 10, name `internal/config` (`readSecret`, `checkEnviron`, `Secret`;
  `decodeStrict`, `readFile`). `SECURITY.md` needs no change, because it already says env or Docker
  secrets only.

## Architecture check

- 0032 (secrets only from environment or Docker secrets): implemented and tightened, not changed. The file
  cannot carry a secret (no secret key exists, and URL user info and queries are rejected), and secrets are
  never in error messages or formatted output. The "never on the command line" half stays true because no
  flag is added.
- 0012 (agent never contacts Telegram): reinforced. The Telegram token in the agent's environment is a
  start-up error.
- 0034 (entry points delegate to `run`): untouched. `cmd/*` do not change in this change.
- 0047 (gocognit 15, no new exclusion without a record): the walker must be split into helpers, with no
  `//nolint`.
- 0045 / wire format: the agent ID rule is shared from `internal/wire`, so the two cannot drift apart.
- Guarantees in `.squad/project.md` (no gaps, hanging collector, backfill alerts): not touched.

## Security considerations

- Area 8: the required/optional rules, the strict source rules, redaction and value-free error messages
  are listed above. Not in scope: clearing the variables from the process environment after reading. The
  agent starts no child processes, and `/proc/<pid>/environ` is readable only by the same UID and root. A
  later feature that spawns processes must pass an explicit environment. The constant-time comparison of
  the token is #40's.
- Area 10: bounded reads (1 MiB file, 4 KiB secret), no alias expansion, rejection before `open` of
  anything but regular files (no FIFO hang), every YAML form enumerated above, and no panics. The
  reflection walker only handles the kinds the schema uses, and any other kind is a "want …" error, never
  a panic. Nesting is bounded by the schema, and the parser's own depth limit applies before that.
- Area 9: the directories are only validated (absolute, clean), not created. Creating them is #38's and
  #13's job.
- Area 11: `backend.url` restricts scheme, host and port form. The restriction to tailnet addresses stays
  with the ACL (0010), see 0049.
- Dependency: `go.yaml.in/yaml/v3` v3.0.5 has no requirements of its own. `govulncheck` must stay clean.

## Decision records

- `docs/decisions/0048-yaml-library-go-yaml-in-yaml-v3.md` (Proposed)
- `docs/decisions/0049-strict-configuration-file-schema-and-errors.md` (Proposed)
- `docs/decisions/0050-secret-sources-rules-and-redaction.md` (Proposed). It refines 0032 without
  superseding it.

## Out of scope / follow-ups

- Wiring into the binaries, a `-config` flag and the exit code on a configuration error: #13 (backend) and
  #30 (agent) already list "configuration via the shared configuration package". No new issue.
- Options of later features (see spec *Out of scope*). Each adds its key, default, example entry, README
  row and test, as the integration surface requires.
- Per-agent tokens on the backend: #89 will supersede the single `VANDOX_AGENT_TOKEN` on the backend.
