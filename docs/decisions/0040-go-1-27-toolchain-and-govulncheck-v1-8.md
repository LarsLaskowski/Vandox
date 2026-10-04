# 0040: Go 1.27 toolchain; `go` directive without a patch version; govulncheck raised to v1.8.0

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #103
- **Supersedes:** —

## Context

`go.mod` declares `go 1.24`. Go supports the two newest minor releases, so Go 1.24 left upstream support
when Go 1.26 shipped (February 2026). Go 1.27 shipped in August 2026, so on 2026-10-04 the supported
releases are 1.26 (1.26.8) and 1.27 (1.27.1). Record 0037 requires a supported toolchain before the first
stable tag, because `govulncheck` does not flag a Go version that is merely out of support.

Record 0038 ties the backend builder image to `go.mod`: the builder tag's `<major>.<minor>` must equal the
`go` directive (the release workflow checks this), and Dependabot ignores `golang` minor and major updates.
So the `go` line, the builder tag and the builder digest have to move together, by hand.

Facts checked on 2026-10-04 in a scratch copy of the repository:

- With `go 1.26` and with `go 1.27`, format, build, `go vet`, `go test -race` and golangci-lint v2.13.1
  are clean. golangci-lint v2.13.1, the version CI uses, is built with go1.27.0.
- The pinned govulncheck tool, `golang.org/x/vuln` v1.1.4 with `golang.org/x/tools` v0.29.0, builds call
  graphs correctly under go1.24.7 and go1.26.0. Under Go 1.27 it panics in SSA construction
  (`panic: unexpected expr: *ast.KeyValueExpr`, `x/tools@v0.29.0/go/ssa/builder.go:958`) as soon as a
  vulnerability reaches symbol-level analysis. With `golang.org/x/vuln` v1.8.0 (`x/tools` v0.50.0) the
  same scan completes under Go 1.27.
- `golang.org/x/vuln` v1.2.0 and later declare `go 1.25.0` or higher (v1.8.0: `go 1.26.0`). While `go.mod`
  said `go 1.24`, the tool could not be updated, and it stayed at v1.1.4.
- `actions/setup-go` with `go-version-file: go.mod` treats a `go` directive without a patch version
  (`go 1.27`) as "latest available 1.27.x" (toolcache, then the versions manifest). With a patch version
  (`go 1.27.0`) it installs exactly that patch. Locally, `GOTOOLCHAIN=auto` resolves `go 1.27` to
  `go1.27.0`.

## Options considered

1. **Target minor version**
   - *Go 1.26*: works with the current govulncheck v1.1.4. But it leaves support when Go 1.28 ships
     (about February 2027), so the next by-hand bump is due in about four months.
   - *Go 1.27*: the newest release, supported until Go 1.29 ships (about August 2027). It needs govulncheck
     raised to v1.8.0, because v1.1.4 crashes under Go 1.27.

   Chosen: Go 1.27. The dependency update is overdue anyway, and only the `go` change made it possible.
2. **Form of the `go` directive**
   - *`go 1.27.0` (patch pinned), optionally with a `toolchain` line*: CI would install exactly that patch
     and keep it until someone edits `go.mod`. `govulncheck` would then report standard-library fixes the
     project never picks up.
   - *`go 1.27` without a patch and without a `toolchain` line*, as before with `go 1.24`: `setup-go`
     takes the newest 1.27.x patch release.

   Chosen: without a patch.
3. **Govulncheck tool version**
   - *Stay on v1.1.4*: rejected, because it crashes under Go 1.27.
   - *Latest release v1.8.0*, with the indirect `golang.org/x/*` modules raised by `go get` and
     `go mod tidy`.

   Chosen: v1.8.0.

## Decision

`go.mod` declares `go 1.27` with no patch version and no `toolchain` directive. The tool dependency
`golang.org/x/vuln` is raised to v1.8.0 with `go get golang.org/x/vuln@v1.8.0` and `go mod tidy`, and
`go.mod` and `go.sum` are committed together. The backend builder in `deploy/backend/Dockerfile` is
`golang:1.27-trixie@sha256:3b77fc618ec235a1ab412de7737f120dd507c57e8d87de4cbb7994fb94275ed5`. That is the
multi-arch index digest read from Docker Hub on 2026-10-04, and it ships Go 1.27.1. The runtime stage is
unchanged. `.squad/stack.md` (*Toolchain*) names Go 1.27. Records 0037 and 0038 stay as they are: their
rules still hold, and `1.24` appears there only as the value at the time.

## Consequences

- The next by-hand bump is due when Go 1.27 leaves support, that is, when Go 1.29 ships. It changes the
  `go` line, the builder tag and the builder digest together (0038).
- Contributors need a golangci-lint built with Go 1.27 or later. The v2.13.1 release binary qualifies; a
  plain `go install …@v2.13.1` does not, because with `GOTOOLCHAIN=auto` it builds with the go1.26
  toolchain named in golangci-lint's own `go.mod`. Older builds refuse to run (*Known pitfalls* in
  `.squad/stack.md`, corrected by this change).
- A local run with `GOTOOLCHAIN=auto` uses `go1.27.0`, so `govulncheck` can report standard-library issues
  locally that are already fixed in the patch CI uses. `.squad/stack.md` documents this pitfall.
- The planned `/proc` reader can use the standard `io/fs.ReadLinkFS` (Go 1.25 and later; implemented by
  `os.DirFS` and `fstest.MapFS`) instead of a link-reading method of its own. The test-double note in
  `.squad/project.md` is corrected to match.
- To revisit: if a future govulncheck needs a newer Go than the one in `go.mod`, the tool and the toolchain
  have to move together again.
