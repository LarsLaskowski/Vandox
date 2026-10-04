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
- `yaml.v3`'s *syntax* error texts can echo document text too (verified with v3.0.5, Challenge 1):
  `a: *S3NT1NEL` gives `yaml: unknown anchor 'S3NT1NEL' referenced`. Several of them carry no line,
  because the library omits the `line N:` prefix whenever its 0-based mark is 0, i.e. for problems on the
  first line (`a: b: c` → `yaml: mapping values are not allowed in this context`; also
  `found unknown escape character`, `found undefined tag handle`). So no `yaml.v3` error text at all is
  passed through (AC7).
- Keys can hold any character, including newlines (`"a\nb": 1` parses to the key `"a\nb"`, verified).
  Echoing an unknown key verbatim would allow log injection (security area 12) (AC5).
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
  *Unknown key names are shown only when safe*: a key segment is echoed only if it is 1–31 bytes of
  `[A-Za-z0-9_-]` (shorter than `MinAgentTokenBytes`, so a pasted agent token is never shown, and a
  Telegram token or PHC hash contains `:` or `$` and never matches). Otherwise `Key` is the dotted path of
  the enclosing section (empty at the top level), `Line` is the key's line, and `Reason` is
  `unknown key (name not shown: …)` with the rule. Test cases: `"a\nb": 1` and `"x‮y": 1` at the top
  level and inside `spool` (`err.Error()` contains no `\n` and no U+202E, `Key` is `""` / `spool`), a
  32-character key `S3NT1NEL` + 24 × `a` (sentinel absent), a block-scalar key `? |` with a sentinel line,
  and a quoted key with a dot, `"a.b": 1` (not shown, since `.` would read as a path separator). Known keys and the secret
  hint are matched before the display decision, so `token: x` still gets the hint.
- [ ] AC6 *Invalid values* fail with a `*KeyError` that names key and line. The value never appears. Table
  per key (accepted forms in *Approach → Value rules*):
  - `agent_id`: empty, 65 bytes, leading `-`, a space, a `/`.
  - `backend.url`: empty; `ftp://h:1`; `h:1` (no scheme); `http://` (no host); `http://u:p@h:1` (user
    info; the sentinel is the password); `http://h:1/?token=x` (query); `http://h:1/?` (empty query);
    `http://h:1/#f` (fragment); `http://h:1/ingest` (path other than empty or `/`); `http://h:0`;
    `http://h:65536`; `http://h:x`; `http://h:+80`; `http:opaque`; a host that is neither an IP literal
    nor a name of `[A-Za-z0-9.-]` after percent-decoding: `http://a%E2%80%A8b:1` (decodes to U+2028),
    `http://a%2Fb:1`, `http://h_1:1`. Accepted: `http://100.64.0.1:8081`,
    `https://nas.tailnet.ts.net`, `http://[fd7a:115c:a1e0::1]:8081`, `HTTP://h:1/` (the scheme is
    case-insensitive).
  - `spool.directory`, `storage.directory`: empty, relative (`spool`), not clean (`/var/lib/../x`,
    `/var/lib/vandox/`).
  - `log.level` (both): `INFO`, `trace`, empty. Accepted: `debug`, `info`, `warn`, `error`.
  - `web.listen`, `ingest.listen`: `8080` (no colon), `:0`, `:65536`, `:http` (named port),
    `:+80` and `[::1]:+80` (sign; `strconv.Atoi` would accept it), `:-1`, `: 80`, `:808080` (more than
    5 digits), `localhost:8080` (host name), `[::1]:x`. Accepted: `:8080`, `0.0.0.0:8080`, `[::]:8081`,
    `192.168.1.10:8080`.
  - `web.listen` and `ingest.listen` with the same port (`:8080` and `0.0.0.0:8080`) fail with a
    `*KeyError` on `ingest.listen`.
  - A value of the wrong YAML kind for a string option (a mapping, a sequence) and an explicit null
    (`agent_id:`, `agent_id: ~`) fail with a `*KeyError` on that key.
  - A string value containing a character of Unicode category Cc, Cf, Zl or Zp fails: `"a\x01b"`, a
    block scalar with a newline, `"a b"` (Zl), `"a b"` (Zp), `"a‮b"` and `"﻿a"` (Cf),
    each as `log.level` and as `spool.directory` (`"/var/lib/‮x"`), with the sentinel absent.
- [ ] AC7 *Unsupported YAML constructs* fail with a `*KeyError` that carries the line (and the key where
  there is one): a duplicate key (top level and within a section, line of the second occurrence); a second
  YAML document (`---`); an anchor or alias (`&a`/`*a`); a merge key (`<<: *a` and `<<: {…}`); a
  non-string key (`? [a]`); a custom or unsupported tag (`!env X`, `!!binary aGk=`, `!foo {a: 1}`); a
  top-level sequence or scalar. A file that holds only `---` or `~` counts as empty (AC4).
  *Second document after an empty or null first one*: `~` / `---` / `agent_id: x`, `---` / `---`, and
  `agent_id: x` / `---` (a trailing empty document) fail with `Line` = the line of the second `---` (2 in
  each case). The check that a second `Decode` returns `io.EOF` runs whatever the first document holds.
  *Tag sibling forms*, the allowlist being checked on the resolved `Node.Tag` (verified with v3.0.5, the
  resolved tag in parentheses): `%TAG !! tag:evil.example,2000:` / `---` / `a: !!str x`
  (`tag:evil.example,2000:str`), `%TAG !e! tag:yaml.org,2002:` / `---` / `a: !e!binary aGk=` (`!!binary`),
  `a: !<tag:yaml.org,2002:binary> aGk=` (`!!binary`), `a: !!set {x}` (`!!set`) and `a: !!omap [{x: 1}]`
  (`!!omap`) are rejected. `a: !<tag:yaml.org,2002:str> x` and `a: ! x` resolve to `!!str` and are
  accepted. The tag text is never shown (sentinel in a `%TAG` prefix and in a `!S3NT1NEL` local tag).
  *Null tag with content*: a scalar tagged `!!null` whose value is not one of `""`, `~`, `null`, `Null`,
  `NULL` fails, both on a section (`spool: !!null S3NT1NEL`, sentinel absent, defaults not silently kept)
  and on a leaf (`agent_id: !!null x`). `spool: !!null` and `spool: !!null ~` keep the defaults.
  *Errors reported by the YAML parser itself* (syntax errors, and an alias to an undefined anchor, which
  `yaml.v3` rejects while parsing, before any node exists) are also a `*KeyError`, with `Key` empty,
  `Reason` exactly `not valid YAML (syntax error or alias to an undefined anchor)` and `Line` taken from
  the library's `yaml: line N: ` prefix when present, else 0. No `yaml.v3` text is passed through and the
  error wraps nothing (`errors.Unwrap(err) == nil`). Cases, each asserting type, `Line` and that the
  sentinel is absent: `a: [` (Line 1), `a: *S3NT1NEL` (undefined alias; Line 0; sentinel absent),
  `a: b: c` (Line 0, message still begins `config: <file>:`), `a: "\q"`, `a: !x!y z`,
  `a: 1` + newline + `S3NT1NEL: [` (Line 2), and `a: 1` / `---` / `b: [` (a syntax error in the second
  document, Line 3). Line values verified with v3.0.5.
- [ ] AC8 *File errors* return an error that contains the path and is not a `*KeyError`: missing file
  (`errors.Is(err, fs.ErrNotExist)`), a directory, `/dev/null` (not a regular file) and a file of
  `MaxFileBytes+1` bytes. A file of exactly `MaxFileBytes` bytes made of
  comment lines loads (agent: then fails on `agent_id` required, which shows it was read).
- [ ] AC9 *Secret sources* (`VANDOX_AGENT_TOKEN` as the example, same function for all three):
  - value from `VANDOX_AGENT_TOKEN` → `Secrets.AgentToken.Value()` equals it;
  - value from `VANDOX_AGENT_TOKEN_FILE=<abs path>` → equals the file content; content `tok\n` and
    `tok\r\n` give `tok`. `tok\n\n`, `tok\r` and `tok \n` are rejected;
  - both set → `*SecretError` naming both variables;
  - `_FILE` set to an empty value or a non-absolute value → `*SecretError{Var: "VANDOX_AGENT_TOKEN_FILE"}`
    with the reason "must be an absolute path", **no `os` call is made** and the value is never shown.
    Cases: `""`, `rel/x`, and `S3NT1NEL-value` (a secret pasted into the `_FILE` variable): `err.Error()`
    does not contain the sentinel and `errors.Unwrap(err) == nil`;
  - `_FILE` set to an absolute path that is a missing file, a directory, `/dev/null`, a file of
    `MaxSecretBytes+3` bytes, or an empty file → `*SecretError{Var: "VANDOX_AGENT_TOKEN_FILE"}` with the
    rule. **The path is never shown either** (a base64 secret can begin with `/`): a test uses the missing
    path `<tempdir>/S3NT1NEL-value` and asserts the sentinel is absent. For an `os` error, `Err` is only the
    errno taken from the `*fs.PathError` (so `errors.Is(err, fs.ErrNotExist)` holds for the missing file
    and `errors.As(err, new(*fs.PathError))` is false). A file of `MaxSecretBytes` printable characters plus
    `\r\n` is accepted;
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
  `VANDOXX`) are ignored. *Unknown names are shown only when safe*: the name is put into `Var` only if it
  is 1–64 bytes of `[A-Za-z0-9_]`. Otherwise `Var` is empty and `Reason` is `unknown VANDOX_ variable (name
  not shown: …)` with the rule. Cases: `VANDOX_A\nB=x`, `VANDOX_‮X=x` (U+202E), `VANDOX_A-B=x`, and
  `VANDOX_` + 58 × `A` (65 bytes); `err.Error()` contains no `\n`, no U+202E and not the name.
- [ ] AC12 *Redaction*: for a `Secret` holding a sentinel, none of `fmt.Sprintf` with `%v %+v %#v %s %q %x
  %X %d`, `fmt.Sprint`, `fmt.Sprintf("%+v", agent)` / `("%#v", agent)` of a loaded `*Agent` and of a
  `Backend` value, `json.Marshal(agent)`, `slog` text and JSON handler output of `slog.Any("cfg", agent)`
  and of `slog.Any("s", secret)` contains the sentinel. Each contains `[redacted]`. `Value()` returns the
  sentinel. The zero `Secret` has `IsSet() == false`.
- [ ] AC13 *Error order and types*: when a file has two problems, the first in document order is reported.
  Structure and value errors come before secret and environment errors. `errors.As` finds `*KeyError` and
  `*SecretError`. `KeyError.Error()` has the form `config: <file>:<line>: <key>: <reason>` (`<file>:` alone
  when `Line` is 0, and no `<key>: ` when `Key` is empty). `SecretError.Error()` has the form
  `config: <var>: <reason>` (`config: <reason>` when `Var` is empty), followed by `: <err>` when `Err` is
  set.
- [ ] AC14 (`internal/wire`) *Shared agent ID rule*: `wire.ValidateAgentID` accepts `web-1`, `a`, 64 × `a`,
  `A.b_c-1`, and rejects ``, 65 × `a`, `-a`, `.a`, `a b`, `a/b`, `ä` with a `*model.FieldError{Field:
  "agent_id"}`. The existing `Header.Validate` tests keep passing unchanged.

## Approach

### Package layout (`internal/config`)

- `config.go`: the package doc, constants, `KeyError`, `readFile` (bounded read of a regular file) and the
  value checks shared by both binaries (directory, listen address, log level, Cc/Cf/Zl/Zp characters in strings).
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
| Single document `---` / explicit null `~` ✓ (`!!null` scalar) | Empty configuration (AC4), provided no second document follows (next row). |
| Second document after `---` ✓ (parsed without error), including after a null or empty first document (`~` / `---` / `agent_id: x` ✓: document 0 is `!!null`, document 1 a mapping) and a trailing empty one (`a: 1` / `---` ✓) | `*KeyError` on the line of the second document's `---` (the document node's `Line`) (AC7). Checked by a second `Decode` that must return `io.EOF`. This check runs **unconditionally** after the first `Decode` succeeded, before the first document's content is interpreted, so a null first document cannot skip it. |
| Directives: `%YAML 1.1` ✓ accepted; `%YAML 1.2` ✓ and unknown `%FOO` ✓ fail inside the parser; `%TAG` ✓ re-maps a handle (`!!` or `!e!`) | `%YAML 1.1`: no effect. Parser failures: parser-error row. `%TAG` only changes the resolved `Node.Tag`, so the tag allowlist (below), which is applied to `Node.Tag`, covers every re-mapping (AC7). |
| Top level a sequence or non-null scalar | `*KeyError` "top level must be a mapping" (AC7). |
| Block and flow mappings (`a: {b: c}`), quoted keys (`"agent_id":`) | Same nodes, accepted. Quoted keys match by their value. |
| Key case variants (`Agent_ID`) | Unknown key (case-sensitive) (AC5). |
| Duplicate key ✓ (`yaml.Node` keeps both) | `*KeyError` on the second occurrence (AC7). |
| Non-scalar key (`? [a]`) ✓ / non-string scalar key (`1:`, `true:`) | Non-scalar key: `*KeyError` "key must be a string". A scalar key is matched by its text and is therefore unknown (AC5/AC7). |
| Merge key `<<` ✓ (tag `!!merge`) | Rejected as an unsupported construct, not merged (AC7). |
| Anchors `&a` and aliases `*a` ✓ (incl. self-referencing `&a [*a]`, which `yaml.v3` parses without error) | Any node with an `Anchor` or of kind `AliasNode` → `*KeyError` (AC7). There is no alias expansion, so no "billion laughs". |
| Alias to an undefined anchor (`a: *x`) ✓ (fails inside the parser, `unknown anchor 'x' referenced`, no line) | Parser-error row below: `*KeyError`, `Key` empty, generic reason, the anchor name never shown (AC7). |
| Key containing any character outside `[A-Za-z0-9_-]` (newline from `"a\nb"` ✓ or a block-scalar key `? \|`, other Cc, Cf such as U+202E, `.`, `:`, `$`, non-ASCII) or longer than 31 bytes | Matched against the schema first (no schema key contains such a character, so it is unknown). Reported as unknown with the enclosing section's path as `Key` and the name not shown (AC5). |
| Tags: the resolved `Node.Tag` is one of `!!str !!int !!bool !!float !!null !!timestamp !!map !!seq` — implicit, explicit (`!!str x`), verbatim (`!<tag:yaml.org,2002:str> x` ✓) or non-specific (`! x` ✓), all of which `yaml.v3` normalises to these short forms | Accepted, and the leaf rules apply. The allowlist is a comparison of `Node.Tag` with these eight strings; it never looks at the source text or `Node.Style`. |
| Any other `Node.Tag` ✓: custom (`!foo bar` decodes to `"bar"`; `!env X`), other core-like tags (`!!binary` base64-decodes, `!!set` ✓, `!!omap` ✓, `!!pairs`), a `%TAG`-re-mapped `!!` (`tag:evil.example,2000:str` ✓), a re-mapped handle or verbatim form of `!!binary` (normalised to `!!binary` ✓) | `*KeyError` "unsupported tag" on any node, mapping and sequence nodes included (AC7). The tag text is never shown. |
| Scalar tagged `!!null` (implicitly or explicitly) whose value is not a null spelling of the core schema (`""`, `~`, `null`, `Null`, `NULL`): `spool: !!null x` ✓ gives `Tag !!null`, `Value "x"` | `*KeyError` "null tag with a value" on that key, never treated as null (AC7). The value is not shown. |
| Section key with null value (`spool:`, `spool: ~`, `spool: !!null`) | Defaults of that section kept (AC4). |
| Leaf with null value (`agent_id:`) | `*KeyError` "has no value" (AC6). |
| Leaf given a mapping or sequence | `*KeyError` "want a string" (more generally: the description of the field's type) (AC6). |
| Leaf scalar not decodable into the field type (only reachable with non-string fields, covered by a white-box test struct in `decode_test.go` with `int`, `bool`, `time.Duration` and `[]string` fields) | `*KeyError` "invalid value, want <type description>". `yaml.v3`'s error text is discarded (AC6). |
| Plain scalars `5`, `true` for a string option | Taken as their text (`"5"`), then the option's own rule applies. |
| Block scalars (`|`, `>`) and quoted strings with escapes (`"a\x01b"`, `" "`, `"‮"`) ✓ | Decoded, then any character of Unicode category Cc (includes `\n`, `\t`, DEL, C1), Cf (U+202E, U+FEFF, U+200B, U+00AD), Zl (U+2028) or Zp (U+2029) → `*KeyError` (AC6). |
| Nesting deeper than the schema | The leaf rule ("want a string") applies at the first level below a leaf. |
| File > `MaxFileBytes` (1 MiB), not a regular file, missing | `readFile` error with the path (AC8). Bounded read with `io.LimitReader(MaxFileBytes+1)`. |
| Any error from `yaml.v3` while parsing into a `yaml.Node` (scanner/parser syntax errors, undefined alias; in the first and in a second document) ✓ — its text may quote document text and lacks a line for problems on line 1 | `*KeyError{File: path, Line: N, Key: "", Reason: "not valid YAML (syntax error or alias to an undefined anchor)"}`. `N` comes from the prefix `yaml: line N: ` (parsed with `strconv.Atoi` on the digits after that exact prefix, `decode.go:128-129` of v3.0.5), else 0. The library error is dropped, not wrapped. If a later library version changes the prefix, `Line` degrades to 0 and the message stays value-free (AC7). |

Secret-carrying paths through the file, enumerated: the only keys that exist are `agent_id`, `backend.url`,
`spool.directory`, `log.level` (agent) and `web.listen`, `ingest.listen`, `storage.directory`, `log.level`
(backend). No secret field has a `yaml` tag name (secret fields carry `yaml:"-"`, and the walker only
matches explicit tag names, so no key can reach them). A credential embedded in a value is blocked where a
value could carry one: `backend.url` rejects user info and any query (AC6). The other values are bounded by
their own patterns (`agent_id`, listen address, log level), or they are paths.

### Value rules

- `agent_id` (required): `wire.ValidateAgentID`.
- `backend.url` (required): `url.Parse` succeeds; scheme `http` or `https` (url.Parse lower-cases it);
  `Opaque` empty; `User == nil`; `Host` non-empty with a non-empty `Hostname()`; the (percent-decoded)
  `Hostname()` is either accepted by `netip.ParseAddr` or 1–253 bytes of `[A-Za-z0-9.-]` (so a
  percent-encoded U+2028 or `/` that the raw-string character check cannot see is rejected); the port, if
  present, is 1–5 ASCII digits with a value of 1–65535; `RawQuery == ""` and `!ForceQuery`; `Fragment == ""` and `RawFragment == ""`; `Path` is
  `""` or `"/"`. Plain `http` is allowed because the transport is the tailnet (0010, 0017). The URL is not
  checked to be a tailnet address, because MagicDNS names cannot be verified without resolving them. The
  Tailscale ACL stays the boundary (0049). `url.Parse`'s error text quotes the URL (it could hold user
  info), so it is discarded and only the rule is reported. The same goes for `net.SplitHostPort` and
  `strconv` errors in the other checks.
- `spool.directory`, `storage.directory`: `filepath.IsAbs` and `filepath.Clean(p) == p`.
- `log.level`: exactly one of `debug`, `info`, `warn`, `error`.
- `web.listen`, `ingest.listen`: `net.SplitHostPort` succeeds; host empty or `netip.ParseAddr` succeeds
  (zones allowed); the port is 1–5 ASCII digits (checked byte by byte before conversion, because
  `net.SplitHostPort(":+80")` returns `"+80"` and `strconv.Atoi` accepts the sign ✓) and its value
  (`strconv.ParseUint(p, 10, 16)`) is 1–65535. The two ports must differ (error on `ingest.listen`).
- Every string leaf: no character of Unicode category Cc, Cf, Zl or Zp (checked on the decoded value,
  before the option's own rule).
- Every unknown key: its name is echoed only under the safe-name rule of AC5.

### Accepted forms: secrets from the environment (guard: secrets only from env/files)

Known variables per binary. Agent: `VANDOX_AGENT_TOKEN`, `VANDOX_AGENT_TOKEN_FILE`. Backend: those two
plus `VANDOX_WEB_PASSWORD_HASH`, `VANDOX_WEB_PASSWORD_HASH_FILE`, `VANDOX_TELEGRAM_BOT_TOKEN` and
`VANDOX_TELEGRAM_BOT_TOKEN_FILE`. The input is `environ []string` in `os.Environ()` form.

| Input form | Behavior |
| ---------- | -------- |
| Entry without `=` | Ignored (cannot be set through `os.Setenv`, may appear in a raw environ). |
| Name not starting with `VANDOX_` case-insensitively (`PATH`, `VANDOXX`) | Ignored. |
| Name starting with `VANDOX_` case-insensitively but not known to this binary (typo, lower case, the other binary's secret) | `*SecretError` naming the variable if the name is 1–64 bytes of `[A-Za-z0-9_]`; otherwise (a name may hold any byte except `=` and NUL: newline, U+202E, `-`, non-ASCII, overlong) `Var` is empty and the reason says the name is not shown (AC11). |
| Known name twice | `*SecretError` "set more than once" (AC11). |
| `NAME` and `NAME_FILE` both present | `*SecretError` naming both (AC9). A present but empty value counts as present. |
| Neither present | Unset. The agent token is required on the agent (AC10). |
| `NAME=""` | `*SecretError` "is empty". |
| `NAME=v` | `v` must be 1–`MaxSecretBytes` bytes of printable ASCII `0x21`–`0x7E`. Anything else, including space, tab, CR/LF, NUL, DEL, non-ASCII and a BOM, → `*SecretError` with the reason only (AC9). |
| `NAME_FILE=""` / any value for which `filepath.IsAbs` is false (`rel/x`, a secret pasted into the variable) | `*SecretError{Var: NAME_FILE, Reason: "must be an absolute path"}`, `Err` nil. No `os` call is made, and the value is never shown (AC9). |
| `NAME_FILE=/abs` | `os.Stat` (follows symlinks, so the symlinked Docker/Kubernetes secrets work) must be a regular file. The file may hold at most `MaxSecretBytes+2` bytes (value plus `\r\n`), read through `io.LimitReader(MaxSecretBytes+3)`, and more is a "too large" error. Exactly one trailing `\n` is removed, then one trailing `\r` if that `\n` was removed. The rest must satisfy the value rule. The path is never shown either (a base64 secret can start with `/`, and a path can hold control characters). For an `os` error, `Err` is the errno from the `*fs.PathError` (`errors.As` to `*fs.PathError`, take `.Err`), never the `*fs.PathError` itself, whose text contains the path; any other `os` error is replaced by the reason alone. Content never appears (AC9). |
| Agent token present on either binary | At least `MinAgentTokenBytes` (32) bytes (AC10). |

Error messages name the variable (when its name is safe) and the rule, never the value, a `_FILE` path or
the file content.

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
	Line   int    // 1-based; 0 when the key is missing or the YAML parser reported no line
	Key    string // dotted key path, e.g. "backend.url"; empty for document-level problems and parser
	              // errors; the enclosing section's path for an unknown key whose name is not shown (AC5)
	Reason string // never contains the value or any other document text
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
	Var    string // e.g. "VANDOX_AGENT_TOKEN_FILE"; empty for an unknown name that is not shown (AC11)
	Reason string // never contains the value, a *_FILE path or the file content
	Err    error  // only the errno of an *fs.PathError (e.g. syscall.ENOENT), never the PathError; may be nil
}
func (e *SecretError) Error() string // "config: <Var>: <Reason>[: <Err>]", "config: <Reason>[: <Err>]" if Var == ""
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
  accepted-forms table (AC5 including the safe-name rule, AC6 leaf kinds, AC7 including the parser-error
  cases) and the returned line map.
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
  `decodeStrict`, `readFile`). In area 12, add that configuration error texts never echo document text
  other than schema or safe key names (`decodeStrict`), never a `_FILE` value or path or an unsafe
  variable name (`readSecret`, `checkEnviron`), and that configuration values, although free of Cc, Cf, Zl
  and Zp characters, are logged only as `slog` attributes. `SECURITY.md` needs no change, because it already says env or Docker
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
- Area 12: no error text contains document text that is not a schema key name or a safe unknown key name
  (AC5), so neither a secret nor a control character from the file reaches the journal or `docker logs`.
  `yaml.v3`'s own messages are never passed through (AC6, AC7), and tags are not shown. Secret errors show
  neither a `_FILE` value or path nor an unsafe variable name (AC9, AC11). For the later logging of
  configured values (#13, #30), every string option is free of Cc, Cf, Zl and Zp characters, and
  `backend.url`'s decoded host is an IP literal or an LDH-style name (AC6). Those features should still
  log configuration values only as `slog` attributes, never concatenated into a message; this is noted in
  area 12 of `.squad/project.md`.
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

## Challenge

Devil's Advocate, round 1: 0 major, 3 minor objections. All three accepted; tier unchanged (security).

1. *Syntax errors can echo document text* (minor): **accepted.** Reproduced with v3.0.5:
   `a: *S3NT1NEL` → `yaml: unknown anchor 'S3NT1NEL' referenced`, and `a: b: c`, `a: "\q"`,
   `a: !x!y z` carry no line (the library drops `line N:` when its 0-based mark is 0, `decode.go:114-129`).
   The plan's earlier claim "no document text in the cases tested" is withdrawn. Revised: every error from
   `yaml.v3` while parsing becomes a `*KeyError` with an empty `Key`, a fixed reason and the line parsed
   from the `yaml: line N: ` prefix (else 0); the library error is neither printed nor wrapped (YAML
   accepted-forms table, AC7). AC8 no longer covers syntax errors; AC7 gains the cases the objection asked
   for (sentinel anchor name absent, an error without a line still names the file). Records 0048 and 0049
   updated.
2. *Unknown keys echoed raw, including control characters* (minor): **accepted, and widened.** Verified
   that `"a\nb": 1` yields the key `a\nb`. Instead of checking only Cc, an unknown key's name is shown only
   if it is 1–31 bytes of `[A-Za-z0-9_-]`; otherwise the error names the enclosing section and the line.
   This also covers Cf characters (U+202E), `.` (which would garble the dotted path) and a pasted secret
   used as a key (32+ characters, or containing `:`/`$`). `strconv.Quote` was rejected: it would still copy
   a pasted secret into the log. AC5 gains the cases; record 0049 updated.
3. *Undefined alias is outside the anchor/alias rule* (minor): **accepted.** AC7 now lists `a: *x` (and the
   sentinel form) explicitly, pins it as a `*KeyError` with `Key` empty, `Line` 0 and the fixed reason, and
   asserts the anchor name is absent. The YAML accepted-forms table has a row for it.

## Security review

Plan security review, round 1: CHANGES_REQUIRED, 1 blocking and 6 non-blocking findings. All seven
accepted; tier unchanged (security). Forms marked ✓ were re-verified with v3.0.5 in a scratch module.

1. *AC9 echoes a relative `_FILE` value* (blocking): **accepted, and widened.** AC9 contradicted the
   environment table. Now an empty or non-absolute `_FILE` value gets "must be an absolute path" with no
   `os` call, no wrapped error and the value never shown (AC9 case `VANDOX_AGENT_TOKEN_FILE=S3NT1NEL-value`).
   Security allowed showing absolute paths; the plan goes further and never shows a `_FILE` path, because
   a base64 secret can begin with `/` and an absolute path can hold control characters. `SecretError.Err`
   is only the errno of an `*fs.PathError`, so `errors.Is(err, fs.ErrNotExist)` still works without the
   path in the text (AC9 case with the missing path `<tempdir>/S3NT1NEL-value`). Signature comment of
   `SecretError`, environment table and record 0050 updated; spec *Behavior* updated.
2. *Second document after a null first document* (non-blocking): **accepted.** Verified: `~` / `---` /
   `agent_id: x` parses as a `!!null` document and a mapping, and `a: 1` / `---` yields an empty second
   document. The table now says the second-`Decode`-must-be-`io.EOF` check runs unconditionally, before
   the first document is interpreted; AC7 gains three cases.
3. *Tag sibling forms* (non-blocking): **accepted.** Verified: `%TAG !! tag:evil.example,2000:` turns
   `!!str` into `tag:evil.example,2000:str`; `%TAG !e! tag:yaml.org,2002:` + `!e!binary` and
   `!<tag:yaml.org,2002:binary>` both resolve to `!!binary`; `!!set` and `!!omap` keep their tags. The
   allowlist is stated as a comparison of `Node.Tag` with eight strings, never the source text or
   `Node.Style`; AC7 pins every form, and the tag text is never shown. A *Directives* row was added
   (`%YAML 1.2` and unknown directives fail inside the parser, `%TAG` is covered by the allowlist).
4. *`!!null` with content silently treated as null* (non-blocking): **accepted.** Verified:
   `spool: !!null x` gives `Tag !!null`, `Value "x"`. A `!!null` scalar is null only for the core-schema
   spellings `""`, `~`, `null`, `Null`, `NULL`; any other value is a `*KeyError` (AC7). Considered and
   not chosen: rejecting every explicitly tagged node, which would rely on `Node.Style` (finding 3).
5. *Cf/Zl/Zp in string values, percent-encoded host* (non-blocking): **accepted, both options.** String
   leaves now reject Cc, Cf, Zl and Zp. Verified that `http://a%E2%80%A8b:1` parses with `Hostname()`
   `"a b"`, so `backend.url`'s decoded host must be an IP literal or 1–253 bytes of `[A-Za-z0-9.-]`
   (AC6). Area 12 of `.squad/project.md` additionally says configuration values are logged only as `slog`
   attributes (*Documentation updates*).
6. *Port with a sign* (non-blocking): **accepted.** Verified: `net.SplitHostPort(":+80")` returns `"+80"`
   and `strconv.Atoi` accepts it (`url.Parse` already rejects `http://h:+80`). Ports are 1–5 ASCII digits,
   checked before `strconv.ParseUint(p, 10, 16)`; AC6 gains `:+80`, `[::1]:+80`, `:-1`, `: 80`, `:808080`
   and `http://h:+80`.
7. *Unknown `VANDOX_` names echoed raw* (non-blocking): **accepted.** A name is shown only if it is 1–64
   bytes of `[A-Za-z0-9_]`; otherwise `Var` is empty and the reason says the name is not shown, consistent
   with AC5. `SecretError.Error()` drops the `<var>: ` part then (AC11, AC13, signature comment).
