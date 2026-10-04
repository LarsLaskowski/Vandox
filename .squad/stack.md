# Stack: Go

The toolchain and the exact commands of this repository. Every squad member, skill and instruction file
refers to the entries below by their *italic name*. Seeded from the Squad-Spec-Repository-Template `go`
profile and owned by this repository: keep it true when the build changes.

## Toolchain

- Go `1.27` (module `github.com/LarsLaskowski/Vandox`); `go.mod` names the minor version without a patch,
  so `setup-go` uses the newest 1.27.x available on the runner (toolcache first, then the versions
  manifest).
- `gofmt` as the formatter, `go vet` and **golangci-lint** `v2.13.1` (configured in `.golangci.yml`) as the
  analyzers, `govulncheck` (a `tool` dependency in `go.mod`) for known vulnerabilities.
- The SessionStart hook `.claude/hooks/session-start.sh` runs `go mod download` in remote sessions.

## Layout

- Production code: `cmd/vandox-agent`, `cmd/vandoxd`, `internal/`.
- Tests: colocated `_test.go` files next to the code they cover, one per source file under test
  (`foo.go` → `foo_test.go`).

## Commands

| Name | Command |
| ---- | ------- |
| *Restore* | `go mod download` |
| *Format* (Code Officer only in the squad) | `gofmt -w .` |
| *Format check* | `test -z "$(gofmt -l .)"` |
| *Build* | `go build ./...` |
| *Test* | `go test ./... -race` |
| *Single test* | `go test ./<package> -run '^TestName$'` |
| *Test with coverage* | `go test ./... -race -coverprofile=coverage.out` (overwrites `coverage.out`) |
| *Coverage gate* | `python3 .squad/tools/coverage-check.py` |
| *Analyzer gate* | `python3 .squad/tools/analyzer-check.py` |

## Analyzer gate

`analyzer-check.py` runs `go vet ./...` (must pass for the whole module) and
`golangci-lint run --new-from-merge-base=origin/main --whole-files ./...`, which reports every issue
anywhere in a file changed since the merge base with `origin/main`. Fixable style findings are the Code Officer's; findings that need a
code change go to the Dev or Tester. `gocognit` (threshold 15, `.golangci.yml`) stands in for SonarQube
Cloud's cognitive-complexity rule `go:S3776`. The two measures are close but not identical: in every case
measured (PR #110, `main`) gocognit flagged at least what SonarQube flagged, and it also counts an `if` on
the bare `err != nil` check, which SonarQube does not, so it can flag a function SonarQube accepts. Such a
finding is fixed like any other diagnostic; a new gocognit exclusion — a rule in `.golangci.yml` or a
`//nolint:gocognit` directive — needs a Lead decision record (two existing validators are excluded by
name, without a complexity cap, record 0047). Other SonarQube Cloud findings (further rules,
duplication, hotspots) have no local Go equivalent here and arrive in squad step 11.

## Writing code

- `gofmt` formatting; package names short and lower-case; exported identifiers documented with a comment
  that starts with the identifier's name.
- Errors are returned and wrapped with context (`fmt.Errorf("…: %w", err)`), never ignored; no `panic` in
  library code.
- `context.Context` is the first parameter of anything that does I/O or can block, and is honored.
- Both binaries print their build information through `internal/version`, set via `-ldflags`.

## Writing tests

See `docs/UNIT_TESTS.md`: the standard `testing` package, table-driven tests with `t.Run`, `t.Helper()` in
helpers, `t.TempDir()` for files, failure messages that state got and want.

## Skeleton

New functions and methods with their full signature and doc comment, bodies
return a zero value or an error (`errors.New("not implemented")`) where the signature allows it, and
`panic("not implemented")` only where it does not (a `panic` aborts the whole test binary), so the module
builds and the tests compile and fail.

## Dependencies

`go get <module>@<version>`, then `go mod tidy`; commit `go.mod` and `go.sum` together. A new dependency is
a `security`-tier change.

## Concurrency

Builds and test runs share the Go build cache safely, but run one squad member that builds or tests at a
time so results are attributable.

## Known pitfalls

- `golangci-lint` must be on the PATH; the version in CI (`.github/workflows/ci.yml`) is the reference.
  It refuses to run ("the Go language version used to build golangci-lint is lower than the targeted Go
  version") when it was built with an older Go than `go.mod` targets. A plain
  `go install github.com/golangci/golangci-lint/v2/cmd/golangci-lint@<version>` does not avoid this: with
  `GOTOOLCHAIN=auto` it builds with the Go version from golangci-lint's own `go.mod`, which can be older
  than this module's. Use the binary from the golangci-lint GitHub release, or force a toolchain at least
  as new as `go.mod` (`GOTOOLCHAIN=go1.27.<n> go install ...@<version>`); `golangci-lint version` shows
  the Go it was built with.
- With `GOTOOLCHAIN=auto`, a local Go older than `go.mod` switches to the `.0` release of that minor
  version (`go 1.27` -> `go1.27.0`). `go tool govulncheck ./...` can then report standard-library
  vulnerabilities that the current patch, the one CI uses, already fixes. Select the current patch
  (`GOTOOLCHAIN=go1.27.<n>`) before trusting a local scan.
