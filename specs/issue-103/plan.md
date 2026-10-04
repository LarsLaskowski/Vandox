# Plan: Move to a supported Go toolchain

Source: Issue #103
Status: Draft
Tier: security — the change raises the toolchain in `go.mod`, edits the Docker builder base image and updates a dependency (`golang.org/x/vuln`); `.squad/routing.md` places all three in `security`.

## Problem / root cause

`go.mod:3` declares `go 1.24`. Go supports the two newest minor releases, so 1.24 left support when Go 1.26
shipped. `deploy/backend/Dockerfile:4` pins `golang:1.24-trixie@sha256:5835f052…` to match. Record 0037
requires a supported toolchain before the first stable tag, and `docs/CONTRIBUTING.md:142-145` repeats
that requirement.

Claims of the issue, checked against the code and the upstream registries on 2026-10-04:

| Claim | Result |
| ----- | ------ |
| `go.mod` declares `go 1.24`; 1.24 is out of upstream support | Confirmed (`go.mod:3`). |
| "Go 1.26 shipped Feb 2026", which implies 1.26 is the current release | **Partly refuted.** Go 1.27 shipped in August 2026. `proxy.golang.org` lists go1.27.0 and go1.27.1, and `golang:1.27-trixie` exists. The supported releases are 1.26 (1.26.8) and 1.27 (1.27.1). |
| Dependabot ignores `golang` minor/major on purpose (record 0038) | Confirmed: `.github/dependabot.yml` `ignore` for `golang` `semver-major`/`semver-minor`; record 0038 option 5. |
| The release workflow fails if the builder's Go minor differs from `go.mod` | Confirmed: `.github/workflows/release.yml:156-169`, step *Check builder Go version*. It compares `<major>.<minor>` only. |
| `.github/workflows/ci.yml:85` refers to the old version | **Refuted.** The comment "go.mod "tool" dependency (go 1.24+)" names the Go release that introduced `tool` directives. That is still true with Go 1.27, so the line stays unchanged. |
| `docs/decisions/0038` lines 45 and 64 mention 1.24 | Confirmed. The record is accepted and append-only, and its rule ("builder follows `go.mod`") is unchanged. 1.24 appears there only as the value at the time, so it is not edited. Record 0040 records the new value. |
| `.squad/stack.md:9` says Go `1.24` | Confirmed. It must change. |
| `.squad/project.md:149`: "Go 1.24's `io/fs` has none" (no link-reading interface) | True for 1.24, **stale after this change.** Go 1.25 added `io/fs.ReadLinkFS`. Under go1.26.8 `os.DirFS("/proc")` implements it (`ReadLink("self/exe")` works), and `fstest.MapFS` has `ReadLink`. The row must be corrected. |

Related defects found on the way:

1. **govulncheck stuck on v1.1.4, and it crashes under Go 1.27.** `golang.org/x/vuln` v1.2.0 and later
   declare `go 1.25.0` or higher (v1.8.0: `go 1.26.0`), so the tool could not be updated while `go.mod`
   said 1.24. v1.1.4 (`x/tools` v0.29.0) panics in SSA construction under Go 1.27:
   `panic: unexpected expr: *ast.KeyValueExpr` at `x/tools@v0.29.0/go/ssa/builder.go:958`. I reproduced
   this with a synthetic stdlib vulnerability in a local `-db file://` database, which forces call-graph
   analysis. Under Go 1.24.7 and 1.26.0, v1.1.4 completes. With x/vuln v1.8.0, the scan completes under
   Go 1.27. Moving to 1.27 therefore needs the tool raised too, otherwise the CI `govulncheck` job and the
   release `Vulnerability scan` step would break as soon as a reachable vulnerability is published.
2. **`go 1.24` → `go 1.26` with `go mod tidy` alone already changes `go.sum`.** It adds six lines for
   `github.com/google/go-cmdtest`, `go-cmp` and `renameio`, test dependencies of x/vuln that module-graph
   pruning now keeps. `go.mod` and `go.sum` must be committed together.
3. **This sandbox's local golangci-lint is v2.5.0, built with go1.25.1.** It refuses to run against both
   `go 1.26` and `go 1.27`: "the Go language version (go1.25) used to build golangci-lint is lower than the
   targeted Go version". This is the known pitfall in `.squad/stack.md`. The Code Officer must use v2.13.1,
   the CI version, which is built with go1.27.0 and verified to report `0 issues` on the changed module.
   It is environment, not repository content.

Environment facts for this run: local Go is go1.24.7 with `GOTOOLCHAIN=auto`. The `go` command downloads
the toolchain from `go.mod` itself (with `go 1.27` that is go1.27.0). Docker Hub's registry API is
reachable, and the builder digest below was read from it. There is **no Docker daemon** and **vuln.go.dev
is blocked (403)**, so the image build and the online govulncheck scan can only be verified in CI
(*Verification without tests*).

## Acceptance criteria

- [ ] AC1: `go.mod` declares `go 1.27`, with no patch version and no `toolchain` directive.
  `golang.org/x/vuln` is at `v1.8.0`, the `tool golang.org/x/vuln/cmd/govulncheck` line is unchanged, and
  `go mod tidy` leaves `go.mod` and `go.sum` unchanged.
- [ ] AC2: The first `FROM` of `deploy/backend/Dockerfile` is exactly
  `FROM golang:1.27-trixie@sha256:3b77fc618ec235a1ab412de7737f120dd507c57e8d87de4cbb7994fb94275ed5 AS build`.
  No other line of the Dockerfile changes. The runtime `FROM` keeps its tag and digest.
- [ ] AC3: `.squad/stack.md` *Toolchain* names Go `1.27`. *Known pitfalls* has the new local-govulncheck
  entry (wording below). `go.mod`, the builder tag and `stack.md` all name 1.27.
- [ ] AC4: `.squad/project.md` row `/proc` no longer claims that `io/fs` has no link-reading interface
  (wording below).
- [ ] AC5: `docs/CONTRIBUTING.md` no longer contains the obsolete section *Before the first stable tag*
  (lines 142-146, inside the `releases` project block). `rg -n '1\.24' --glob '!specs/**'` finds only
  `.github/workflows/ci.yml:85` (correct, see above), `docs/decisions/0038-…` (accepted record, not
  rewritten) and record 0040, which names the old value.
- [ ] AC6: Locally under Go 1.27: *Format check*, *Build*, `go vet ./...`, *Test*, and golangci-lint
  v2.13.1 (*Analyzer gate*) are clean.
- [ ] AC7: In the pull request, CI is green: `build-test-lint`, `Vulnerability scan` (govulncheck against
  the live database), `SonarQube` and CodeQL. The release workflow's dry run is green, including *Check base
  image pinning*, *Check builder Go version*, *Build image* and *Verify image*. Its trigger paths include
  `go.mod`, `go.sum` and `deploy/backend/Dockerfile`, so the PR starts it.
- [ ] AC8: The diff contains no production or test code (`cmd/`, `internal/`, `*_test.go`) and no change
  to any workflow file.

## Verification without tests

The change touches no production or test code, so steps 4 (*Skeleton*), 5 (*Tests first*) and the
*Coverage gate* of step 6 are **not applicable** (`.squad/routing.md`, *Changes without production or
test code*). The tier stays `security`: the plan challenge, step 3 and step 8 run in full. If production
or test code has to change after all (for example a new `go vet` finding), steps 4-6 apply again. I found
none under go1.27.0.

| AC | Verified where | By whom |
| -- | -------------- | ------- |
| AC1 | `head -5 go.mod`; `go mod tidy && git diff --exit-code go.mod go.sum`; `go list -m golang.org/x/vuln` → `v1.8.0` | Dev in step 6, then Code Officer in step 7; Reviewer reads the diff |
| AC2 | Read-only check of the Dockerfile diff (one line). The digest is re-read and compared with the registry, for example `curl` of `https://registry-1.docker.io/v2/library/golang/manifests/1.27-trixie` with an anonymous pull token and `Accept: application/vnd.oci.image.index.v1+json`, checking `docker-content-digest`. If the tag has moved since 2026-10-04, any digest the registry lists for `1.27-trixie` is acceptable, as long as the image is 1.27.x. Also verified by the release dry run (AC7). | Dev; Security re-reads the digest in step 8; CI |
| AC3 | Read-only diff of `.squad/stack.md`; `grep -n '1\.27' go.mod deploy/backend/Dockerfile .squad/stack.md` | Reviewer (step 8) |
| AC4 | Read-only diff of `.squad/project.md` | Reviewer (step 8) |
| AC5 | Read-only diff of `docs/CONTRIBUTING.md`; the `rg` command from AC5 | Reviewer (step 8) |
| AC6 | *Format check*, *Build*, `go vet ./...`, *Test*, *Analyzer gate* with golangci-lint v2.13.1 on the PATH (`go install github.com/golangci/golangci-lint/v2/cmd/golangci-lint@v2.13.1`). Local `go tool govulncheck ./...` cannot reach vuln.go.dev in this sandbox, so state that plainly in the log; it is not a pass. | Code Officer (step 7) |
| AC7 | GitHub Actions on the PR: `CI` (all three jobs), `CodeQL`, `Release` (dry run, `v0.0.0-dryrun`) | Orchestrator reads the check results in step 11. A red check goes back through steps 7-8 |
| AC8 | `git diff --name-only origin/main...HEAD`. Allowed: `go.mod`, `go.sum`, `deploy/backend/Dockerfile`, `.squad/stack.md`, `.squad/project.md`, `docs/CONTRIBUTING.md`, `docs/decisions/0040-…`, `specs/issue-103/*` | Reviewer (step 8) |

## Approach

The Dev runs these commands in this order, so the `go` line is raised before `go get` and keeps its
patch-less form:

```bash
go mod edit -go=1.27
go get golang.org/x/vuln@v1.8.0
go mod tidy
head -3 go.mod   # must show "go 1.27", no patch version, no toolchain line
```

With go1.27.0 in a scratch copy, this produced `go 1.27` and these requirements:
`golang.org/x/mod v0.41.0`, `golang.org/x/sync v0.23.0`, `golang.org/x/sys v0.48.0`,
`golang.org/x/telemetry v0.0.0-20260908163034-4bcc4b2ee518`, `golang.org/x/tools v0.50.0`,
`golang.org/x/vuln v1.8.0`, all `// indirect`, plus the matching `go.sum`. If `go get` writes `go 1.27.0`
or adds a `toolchain` line, reset them with `go mod edit -go=1.27 -toolchain=none`.

Then the Dev makes the edits listed below.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| module | `go.mod` | `go 1.24` → `go 1.27`; indirect `golang.org/x/*` requirements raised by the commands above (x/vuln v1.1.4 → v1.8.0) |
| module | `go.sum` | Regenerated by `go get` and `go mod tidy`; never edited by hand |
| backend image | `deploy/backend/Dockerfile:4` | `FROM golang:1.27-trixie@sha256:3b77fc618ec235a1ab412de7737f120dd507c57e8d87de4cbb7994fb94275ed5 AS build` (index digest, Go 1.27.1, image created 2026-09-19; verified: sha256 of the raw index body equals the `docker-content-digest` header; the same method reproduces the current 1.24 pin `5835f052…`) |
| squad stack (product description) | `.squad/stack.md` | See *Documentation updates* |
| project knowledge | `.squad/project.md:149` | See *Documentation updates* |
| docs | `docs/CONTRIBUTING.md:142-146` | Remove the section; see *Documentation updates* |
| CI | `.github/workflows/*.yml` | **No change.** Every `setup-go` reads `go-version-file: go.mod`. The comment at `ci.yml:85` is correct as written. golangci-lint v2.13.1 is built with go1.27.0 |

## Signatures (for the Dev's skeleton)

None. No production or test code changes.

## Test files

None. No production or test code changes (see *Verification without tests*). No existing test calls a
changed signature.

## Documentation updates

The Dev makes all of these.

1. `.squad/stack.md`, *Toolchain*, line 9: change
   `` - Go `1.24` (module `github.com/LarsLaskowski/Vandox`). `` to
   `` - Go `1.27` (module `github.com/LarsLaskowski/Vandox`); `go.mod` names the minor version without a patch, so `setup-go` uses the newest 1.27.x. ``
2. `.squad/stack.md`, *Known pitfalls*: append this bullet:
   `` - With `GOTOOLCHAIN=auto`, a local Go older than `go.mod` switches to the `.0` release of that minor version (`go 1.27` → `go1.27.0`). `go tool govulncheck ./...` can then report standard-library vulnerabilities that the current patch, the one CI uses, already fixes. Select the current patch (`GOTOOLCHAIN=go1.27.<n>`) before trusting a local scan. ``
3. `.squad/project.md:149`, row `/proc`: replace
   `` symlink targets such as `/proc/<pid>/fd/*` through a link-reading method on the same small interface, since Go 1.24's `io/fs` has none `` with
   `` symlink targets such as `/proc/<pid>/fd/*` through `io/fs.ReadLinkFS` (Go 1.25 and later; implemented by `os.DirFS` and `fstest.MapFS`, so the blocking wrapper implements it too) ``.
   The rest of the row stays.
4. `docs/CONTRIBUTING.md`: delete lines 142-146 (the heading `### Before the first stable tag` at 142,
   the blank line at 143, the two-line paragraph at 144-145 and the blank line at 146), so that line 141
   (blank) is followed directly by `### Re-running a failed release`. This change fulfils that requirement. Keep the
   `<!-- project:… releases -->` markers intact.
5. `README.md`, `docs/ARCHITECTURE.md`: no change. Neither names a Go version. ARCHITECTURE.md:223
   ("only after `govulncheck` passes") stays true.

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is weakened:

- **Release built from source, without caches, only after govulncheck passes** (0037). This is preserved
  and, in fact, kept working: with the tool raised, govulncheck can still analyse code under Go 1.27.
- **Base images pinned by digest; builder minor follows `go.mod`** (0038). This is preserved: the new
  digest is pinned, and the release check enforces 1.27 = 1.27.
- **Both binaries share the Go minor version** (0038 consequence). Preserved.
- The binaries' behavior does not change: no production code changes, and tests pass unchanged under
  go1.27.0.

## Security considerations

- **Supply chain.** The builder digest comes from Docker Hub's official `library/golang` repository. I
  verified it by hashing the raw index body against `docker-content-digest`. Security should re-read it
  independently in step 8. The x/vuln update brings new `golang.org/x/*` versions (official Go
  sub-repositories) and three test-only modules in `go.sum` (`google/go-cmdtest`, `go-cmp`, `renameio`).
  None of them is linked into either binary: they are dependencies of the `tool` only, and
  `go version -m` on the binaries should show no `golang.org/x` modules.
- **Vulnerability gate stays effective.** Without the tool update, govulncheck panics under 1.27 when a
  reachable vulnerability exists. That would turn the release gate into a hard failure exactly when it
  matters (a fail-closed outage, not a bypass).
- **Patch currency.** The `go` line stays without a patch version, so `setup-go` keeps taking the newest
  1.27.x and govulncheck checks against current standard-library fixes. The builder image moves only
  through Dependabot digest updates of `1.27-trixie`, as accepted in 0038.
- No secret, permission, workflow trigger or runtime image changes.

## Decision records

- `docs/decisions/0040-go-1-27-toolchain-and-govulncheck-v1-8.md` (Proposed): the target minor (1.27
  over 1.26), the patch-less `go` directive, and govulncheck raised to v1.8.0. Records 0037 and 0038 are
  neither superseded nor edited: their rules hold. A search for `1\.24` finds this record and 0038 (the
  historical value), plus `ci.yml:85`.

## Out of scope / follow-ups

- `actions/setup-go` prefers a cached 1.27.x on the runner over the newest patch (`check-latest` is not
  set). 0038's consequence "setup-go takes the latest patch release" is therefore slightly optimistic. This
  behavior predates this change, and govulncheck would flag a vulnerable cached patch. No follow-up is
  proposed unless the Devil's Advocate or Security asks for one.
- No other follow-up issue.
