# 0049: Strict configuration file: schema-only keys, no YAML extras, errors name file, line and key but never the value

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Issue #11
- **Supersedes:** —

## Context

Issue #11 asks for strict parsing ("unknown keys and invalid values fail at start-up with a message
naming the key") and sensible defaults. It also asks for commented example files under `deploy/agent/`
and `deploy/backend/` that a test loads. `docs/ARCHITECTURE.md` (*Configuration*) says both binaries are
configured through a configuration file and environment variables. Secrets come only from the
environment (0032, 0050). The backend skeleton (#13) and the agent skeleton (#30) will call the loader at
start-up. Neither binary has a start action yet (0072).

The YAML library accepts more than a configuration file needs (0048). An option that is silently ignored,
for example a duplicate key whose first value wins or a second document, is exactly what "strict" is meant
to prevent. Error texts end up in the journal or in `docker logs`. A secret pasted into the wrong key must
not be copied there.

## Options considered

1. **Strict schema walk over the node tree**: only keys that are fields of the option structs (explicit
   `yaml` tag names) are accepted. Duplicate keys, several documents, anchors and aliases, merge keys,
   non-string keys and tags outside the core schema are errors. Errors carry the file, the line and the
   dotted key, but not the value. The library's value errors are discarded. This gives the most control
   and the clearest messages, at the cost of a small reflection walker of our own.
2. **The library's strict mode** (`KnownFields(true)`). It is less code, but duplicate keys are only
   caught when decoding into Go values, and extra documents, aliases and custom tags are accepted. The
   messages name a Go type instead of the dotted key and quote the offending value.
3. **Environment overrides for every option** (`VANDOX_SPOOL_DIRECTORY`, …). This is common for
   containers, but it gives each option two sources of truth, the strict file is no longer the whole
   configuration, and every option doubles its documentation and tests.
4. **Every error collected and reported at once** (`errors.Join`). This is convenient for the operator,
   but it makes "which error is reported" harder to pin in tests, and a start-up check rarely finds more
   than one mistake.
5. **Pass the YAML library's syntax error message through** (wrapped with the file name). It is the most
   detailed description, but the messages can quote document text (`a: *S3NT1NEL` gives
   `unknown anchor 'S3NT1NEL' referenced`), so a token pasted unquoted after a `*` would reach the logs.
   Rejected in favor of a fixed reason plus the line number.
6. **Show unknown key names quoted** (`strconv.Quote`). This neutralizes newlines and other control
   characters, but still copies a secret pasted as a key into the logs. Rejected in favor of showing a name
   only when it is short and made of safe characters.
7. **Reject only control characters (Cc) in string values** and leave log safety to the features that
   log the values. It is the narrower rule, but U+2028/U+2029 and bidirectional overrides (U+202E) would
   pass and could split or disguise a log line later. Rejected; Cf, Zl and Zp are rejected as well.

## Decision

Option 1, with these specifics:

- YAML constructs: exactly one document. A second document is rejected even when the first one is null
  or empty, and a trailing `---` counts as a second document. The tag allowlist (`!!str !!int !!bool
  !!float !!null !!timestamp !!map !!seq`) is checked on the tag the parser resolved for each node, never
  on the source text, so `%TAG` re-mappings, verbatim tags (`!<tag:yaml.org,2002:binary>`), `!!set` and
  `!!omap` are rejected. A `!!null`-tagged scalar is null only for `""`, `~`, `null`, `Null` and `NULL`;
  `!!null x` is an error rather than a silently ignored value. A rejected tag is not shown.
- `internal/config` owns the loading. `LoadAgent(path, environ)` and `LoadBackend(path, environ)` read
  the file at `path` and the environment `environ` (in `os.Environ()` form). Default paths are
  `/etc/vandox/agent.yaml` and `/etc/vandox/vandoxd.yaml` (mounted into the container). The binaries are
  wired to it by #13 and #30, not here.
- The file must be a regular file (checked before opening, so a FIFO cannot block start-up) of at most
  1 MiB. An empty file, a file with only comments, or a document that is just `---` or `~` means
  "defaults only".
- Only keys of the schema are accepted, and they are case-sensitive. A section key with a null value keeps
  the section's defaults, while a leaf with a null value is an error. String values may not contain
  characters of Unicode category Cc, Cf, Zl or Zp (control and format characters such as U+202E, line and
  paragraph separators), so the values #13 and #30 will log cannot break a log line or reorder its
  display (option 7 rejected). Non-secret options exist only in the file, with no environment overrides (option 3
  rejected).
- The first error is reported (option 4 rejected): structure and values in document and field order, then
  the environment and secrets. `*KeyError` (`File`, `Line`, `Key`, `Reason`) carries file, line and key.
  The value never appears in the message. A key that looks like a secret (`token`, `password`, `secret` in
  its last segment) gets a hint that secrets belong in environment variables.
- No other text from the file appears in an error either. An unknown key's name is shown only if it is
  1–31 bytes of `[A-Za-z0-9_-]` (shorter than the minimum agent token, so a pasted token is never shown);
  otherwise the error names the enclosing section and the line (option 6 rejected). Errors the YAML
  parser reports itself (syntax errors, an alias to an undefined anchor) become a `*KeyError` with an
  empty key, a fixed reason and the line from the library's `yaml: line N: ` prefix, or no line when the
  library gives none (it omits it for problems on the first line); the library's message is neither
  printed nor wrapped (option 5 rejected).
- Options in this change (others arrive with their features through the integration surface in
  `.squad/project.md`):
  - agent: `agent_id` (required, the wire format's agent ID rule via `wire.ValidateAgentID`),
    `backend.url` (required), `spool.directory` (`/var/lib/vandox/spool`) and `log.level` (`info`);
  - backend: `web.listen` (`:8080`), `ingest.listen` (`:8081`, a different port), `storage.directory`
    (`/data`) and `log.level` (`info`).
- `backend.url` must be `http` or `https` with a host, an optional port from 1 to 65535, no user info, no
  query, no fragment and an empty path or `/`. After percent-decoding, the host must be an IP literal or
  consist of letters, digits, `.` and `-` (a percent-encoded U+2028 or `/` is rejected). Plain `http` is allowed because the tailnet encrypts and
  authenticates the transport (0010, 0017). Whether the host is a tailnet address is not checked, because
  a MagicDNS name cannot be verified without resolving it, so the Tailscale ACL remains the boundary.
- Listen addresses are `host:port` with an empty host or an IP literal and a port of one to five ASCII
  digits (no sign: `:+80` is rejected although `strconv.Atoi` would accept it). Host names are
  rejected, because binding to a name is ambiguous.
- Directories must be absolute and clean. They are not created here.
- The loader takes no `context.Context`. It reads one local regular file and at most three small secret
  files at start-up, and such reads cannot be cancelled.
- The example files `deploy/agent/agent.yaml` and `deploy/backend/vandoxd.yaml` set every option
  explicitly, with comments. Tests load them and check, through `AgentKeys()`/`BackendKeys()`, that every
  option is present and that the optional ones equal the defaults, so the examples cannot drift from the
  code.

## Consequences

- A typo or a leftover option stops start-up instead of being ignored, so upgrades that remove an option
  will need a note in the release notes.
- Configuration files cannot use anchors, aliases or `!env`-style tags. For files this small that is
  acceptable.
- Each new option must add its key, default, example entry, README row and test (the integration surface
  in `.squad/project.md`). The example-coverage test fails if the example entry is missing.
- Containers that want to change a non-secret option mount a file. There is no environment shortcut. If
  that proves impractical, a later record can supersede this one with explicit, enumerated overrides.
