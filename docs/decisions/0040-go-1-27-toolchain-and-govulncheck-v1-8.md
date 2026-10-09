# 0040: Go 1.27 toolchain; `go` directive without a patch version; govulncheck raised to v1.8.0

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issue #103
- **Supersedes:** —

## Context

`go.mod` declared `go 1.24`, which left upstream support when Go 1.26 shipped. On 2026-10-04 the supported releases were
1.26 and 1.27, and `govulncheck` does not flag a Go version that is merely out of support (0037), so the toolchain has to be
moved by hand. The pinned `govulncheck` (`golang.org/x/vuln` v1.1.4, `x/tools` v0.29.0) panics in SSA construction under
Go 1.27 as soon as a vulnerability reaches symbol-level analysis; v1.8.0 completes the same scan, but needs `go 1.26` or
newer in `go.mod`, so it could not be updated while `go 1.24` stood. `actions/setup-go` with `go-version-file: go.mod`
treats `go 1.27` as "newest 1.27.x" and `go 1.27.0` as exactly that patch. Format, build, vet, race tests and golangci-lint
were clean under both 1.26 and 1.27.

## Options considered

- **Go 1.26** — rejected: works with the old govulncheck, but leaves support when Go 1.28 ships (about February 2027).
- **Go 1.27** — chosen: supported until Go 1.29 (about August 2027); needs govulncheck v1.8.0, which was overdue anyway.
- **`go 1.27.0` with a pinned patch** — rejected: CI would keep that patch until someone edits `go.mod`, and `govulncheck` would
  report standard-library fixes the project never picks up. **`go 1.27` without a patch or `toolchain` line** — chosen.
- **Staying on govulncheck v1.1.4** — rejected: it crashes under Go 1.27. **v1.8.0** — chosen.

## Decision

`go.mod` declares `go 1.27` with no patch version and no `toolchain` directive. `golang.org/x/vuln` is raised to v1.8.0
(`go get`, `go mod tidy`, `go.mod` and `go.sum` committed together). `.squad/stack.md` (*Toolchain*) names Go 1.27.

## Consequences

- The next by-hand bump is due when Go 1.27 leaves support (when Go 1.29 ships); it changes the `go` line and, if needed,
  the pinned golangci-lint and govulncheck.
- golangci-lint must be built with a Go at least as new as the toolchain CI resolves. v2.13.1 stopped working when
  `setup-go` moved to Go 1.27.2 and was raised to v2.14.0, so a new Go patch can force a linter bump. A plain
  `go install …@<version>` may build with an older toolchain than its release binary (*Known pitfalls* in `.squad/stack.md`).
- A local run with `GOTOOLCHAIN=auto` uses `go1.27.0`, so `govulncheck` can report standard-library issues locally that are
  already fixed in the patch CI uses.
- The `/proc` reader can use `io/fs.ReadLinkFS` (Go 1.25 and later) instead of a link-reading method of its own.
- If a future govulncheck needs a newer Go than `go.mod` names, tool and toolchain move together again.
