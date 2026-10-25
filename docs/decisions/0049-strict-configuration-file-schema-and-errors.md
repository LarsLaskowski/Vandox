# 0049: Strict configuration file: schema-only keys, no YAML extras, errors name file, line and key but never the value

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Configuration and secrets
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

Option 1: the loader walks the parsed YAML tree against the option types. Only keys of the schema are accepted,
every YAML construct that could be silently ignored is an error, errors name file, line and key but never the
value, and non-secret options exist only in the file (options 2 to 7 rejected). The rules, the options with their
defaults and the error texts are in [Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- A typo or a leftover option stops start-up instead of being ignored, so upgrades that remove an option
  need a note in the release notes.
- Configuration files cannot use anchors, aliases or `!env`-style tags. For files this small that is acceptable.
- Containers that want to change a non-secret option mount a file. If that proves impractical, a later record can
  supersede this one with explicit, enumerated overrides.
