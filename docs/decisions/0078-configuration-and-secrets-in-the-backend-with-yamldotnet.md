# 0078: The backend reads its strict configuration with YamlDotNet and the same secret rules

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

[0048](0048-yaml-library-go-yaml-in-yaml-v3.md), [0049](0049-strict-configuration-file-schema-and-errors.md)
and [0050](0050-secret-sources-rules-and-redaction.md) define how the configuration files and secrets are read.
0048 names a Go library; the agent still uses it, the backend cannot.

## Options considered

1. **`Microsoft.Extensions.Configuration` binding** — convenient, but lenient: unknown keys, duplicate keys
   and YAML extras (anchors, tags, merge keys) are accepted.
2. **YamlDotNet's event parser with an own strict validator** — the schema and the error rules of 0049 are
   enforced by code.

## Decision

Option 2. `BackendConfigLoader` (`Vandox.Core.Configuration`) parses the file with YamlDotNet's parser
events and rejects unknown and duplicate keys, anchors, aliases, custom tags, a second document, deep nesting and
files over 1 MiB; errors name the file, line and key, never the value. Secrets come only from `VANDOX_*`
variables or `*_FILE` files with the value rules of 0050, unknown `VANDOX_` variables are rejected, and a
`Secret` type redacts itself in every string conversion and log. The agent configuration (Go) is unchanged.

## Consequences

- Both binaries implement the same rules in different languages; a rule change touches both
  (`.squad/project.md`, integration surface *A new or changed configuration option*).
- 0048 stays valid for the agent; its backend clause no longer applies.
