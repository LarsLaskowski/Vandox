# 0005: Go for agent and backend

- **Status:** Superseded by 0073
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The agent runs permanently on a server with about 2 GB RAM next to Plesk, MariaDB and mail; it must be
small, easy to install and free of a runtime that has to be maintained on the server. The backend runs as
one Docker container on a Docker host (typically a NAS). Both share the data model, the wire format and the log parsing.

## Options considered

1. **Go for both** — static single binaries, low memory, good standard library for HTTP, `/proc` parsing
   and concurrency; one language for shared packages.
2. **Different languages** (e.g. a small agent in Go or Rust, a backend in Python or .NET) — free choice
   per side; shared code (wire format, parsing) would be duplicated.
3. **Rust for both** — smallest footprint; slower development for a one-person project.

## Decision

Option 1: `vandox-agent` and `vandoxd` are written in Go in one module (`github.com/LarsLaskowski/Vandox`),
with shared packages under `internal/`.

## Consequences

- One toolchain (`gofmt`, `go vet`, golangci-lint, govulncheck) and one test convention for the whole
  repository (`.squad/stack.md`).
- The agent is deployed as a single binary; the backend image stays small.
- The SQLite driver choice (cgo or pure Go) has to fit the Go build and the container image.
