# 0078: The backend reads its strict configuration with YamlDotNet and the same secret rules

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Configuration and secrets
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

Option 2: the backend parses the file with YamlDotNet's parser events and enforces the strict schema and the error
rules itself, and reads secrets with the same rules as the agent. The agent configuration (Go) is unchanged. The
rules are in [Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- Both binaries implement the same rules in different languages; a rule change touches both
  (`.squad/project.md`, integration surface *A new or changed configuration option*).
- 0048 stays valid for the agent; its backend clause no longer applies.
