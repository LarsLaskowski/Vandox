# 0048: go.yaml.in/yaml/v3 parses the configuration files, through a node tree, not direct decoding

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #11
- **Supersedes:** —

## Context

Issue #11 asks for one YAML configuration file per binary, parsed strictly: unknown keys and invalid
values fail at start-up with a message that names the key. The Go standard library has no YAML parser,
so a dependency is needed. A new dependency is a `security`-tier change (`.squad/stack.md`).

Measured with the candidate library (v3.0.5) in a scratch module:

- Parsing into a `yaml.Node` accepts duplicate keys, several documents in one file, anchors and aliases
  (including a self-referencing one) and custom tags (`!foo bar` decodes to `"bar"`) without an error.
- Its value-decoding errors quote the value (`` cannot unmarshal !!str `abc` into int ``).
- Its strict mode (`Decoder.KnownFields(true)`) names only the last key segment and the Go type
  (`field x not found in type config.spool`), not the dotted path.

## Options considered

1. **`go.yaml.in/yaml/v3`** — the YAML organisation's maintained continuation of the archived
   `gopkg.in/yaml.v3`: the same code and API, released as v3.0.2 to v3.0.5, with no requirements of its
   own. It is widely used, and `sigs.k8s.io/yaml` builds on it.
2. **`gopkg.in/yaml.v3`** — the same code, but archived and no longer maintained, so it would get no
   security fixes.
3. **`go.yaml.in/yaml/v4`** — only release candidates exist (rc.6). That is too early for a dependency in a
   security-relevant parser.
4. **`github.com/goccy/go-yaml`** — actively developed and has a strict mode, but its code base and API
   surface are larger, its error messages differ, and the strict-decoding gaps listed above would still
   need checking one by one.
5. **`sigs.k8s.io/yaml`** — converts YAML to JSON and decodes with `encoding/json`
   (`DisallowUnknownFields`). That means one more module, which also pulls in a YAML library, and it loses
   line numbers in the errors.
6. **Another format (JSON, TOML)** — JSON has no comments, but the issue requires commented example files.
   TOML needs a dependency too, and the issue names YAML.

## Decision

Option 1, `go.yaml.in/yaml/v3` v3.0.5, used only in `internal/config` and only to parse into a
`yaml.Node`. The loader walks the node tree against the option structs itself (`decodeStrict`, record
0049). It rejects duplicate keys, extra documents, anchors and aliases, merge keys and tags outside the
core schema (checked on the tag the library resolved for each node, which already reflects `%TAG`
directives and verbatim tags). It never passes a `yaml.v3` error text through, neither a value-decoding nor a syntax error:
syntax messages can quote document text (`unknown anchor 'x' referenced`), so a parser error is reported
with a fixed reason and only the line number taken from the library's `yaml: line N: ` prefix.

## Consequences

- One new direct dependency without transitive ones. `govulncheck` covers it, and Dependabot updates it.
- Strictness does not depend on the library's own strict mode, and the behavior is pinned by the
  loader's tests, so changing the library later is local to `decode.go`.
- If `go.yaml.in/yaml/v4` is released as stable and v3 stops receiving fixes, this record is superseded by
  a switch to v4 (an API-compatible node type).
