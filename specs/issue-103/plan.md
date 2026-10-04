# Plan: Move to a supported Go toolchain

Source: Issue #103
Status: Draft (revised after the PR, see *Revision after the PR*)
Tier: security — the change raises the toolchain in `go.mod`, edits both Docker base image lines, the release workflow and the Dependabot configuration, and updates a dependency (`golang.org/x/vuln`); `.squad/routing.md` places all of them in `security`.

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
   the CI version: the release binary is built with go1.27.0 and was verified to report `0 issues` on
   the changed module. A plain `go install …@v2.13.1` does **not** give that: golangci-lint's own `go.mod`
   says `go 1.26.0`, so with `GOTOOLCHAIN=auto` it is built with a go1.26 toolchain and refuses to lint a
   `go 1.27` module. The advice in the existing pitfall (`.squad/stack.md:75-78`) leads into exactly this
   trap and is corrected below (Devil's Advocate objection 2). The local binary is environment, not
   repository content.

Environment facts for this run: local Go is go1.24.7 with `GOTOOLCHAIN=auto`. The `go` command downloads
the toolchain from `go.mod` itself (with `go 1.27` that is go1.27.0). Docker Hub's registry API is
reachable, and the builder digest below was read from it. There is **no Docker daemon** and **vuln.go.dev
is blocked (403)**, so the image build and the online govulncheck scan can only be verified in CI
(*Verification without tests*).

## Revision after the PR: SonarQube `docker:S8431`

PR #107 failed the SonarQube Cloud quality gate. Rule `docker:S8431` ("use either the version tag or the
digest, not both") flags `deploy/backend/Dockerfile:4`, which this change touched, so it is new code
(maintainability D). Line 25, the runtime `FROM`, has the same finding as an older issue. The Product
Manager decided to follow the pattern of the maintainer's other repositories, even if record 0038 has to
change.

Claims checked on 2026-10-04:

| Claim | Result |
| ----- | ------ |
| Both `FROM` lines name a tag and a digest | Confirmed: `deploy/backend/Dockerfile:4` (`golang:1.27-trixie@sha256:3b77fc61…`) and `:25` (`gcr.io/distroless/static-debian13:nonroot@sha256:e2e927ec…`). |
| PlexToJellyfinSync uses `ARG BASE_RUNTIME_IMAGE/_TAG/_DIGEST`, `FROM ${…_IMAGE}@${…_DIGEST}`, re-declared ARGs and OCI `base.name`/`base.digest` labels | Confirmed (`plextojellyfinsync/Dockerfile:1-4, 19-25`; `docs/ARCHITECTURE.md:353`: "pinned by digest, with the tag kept alongside for readability"). **Its build stage is tag-only** (`FROM ${BASE_SDK}`, line 6). That part is not adopted, because 0038 pins the builder too. |
| DockerUpdateGuard uses ARG-based images | Confirmed (`src/DockerUpdateGuard/Dockerfile:1-3, 35-41`). Its runtime `FROM ${BASE_RUNTIME}` is tag-only, and `release.yml` only *records* the digest as a label. It is not a pinning model to copy. |
| The release workflow reads the `FROM` text | Confirmed: `release.yml:142-154` requires `@sha256:<64 hex>` in every `FROM` line. `release.yml:156-169` reads the minor from `^FROM golang:`. Both steps **fail** against an ARG-based Dockerfile, so they must change together with it. |
| Dependabot can keep updating ARG-based `FROM` lines | **Refuted.** In dependabot-core's `docker/lib/dependabot/docker/file_parser.rb`, `FROM_LINE` requires the image name to start with `[a-z\d]` (`NAME_COMPONENT` in `docker/lib/dependabot/shared/shared_file_parser.rb`). `ARG` lines are never read. A `FROM ${…}` line yields no dependency, so Dependabot proposes neither tag nor digest updates for this Dockerfile. PlexToJellyfinSync's `docker` Dependabot entry is therefore a no-op too. |
| Record 0037 depends on the `FROM` form | Refuted: 0037 mentions Dependabot only for action SHAs, and digests only for the *published* image. It stays unchanged. |

Related defect found on the way: once Dependabot no longer refreshes the builder digest, the builder's
Go patch can fall behind the runner's. The release `govulncheck` scans the source with the runner's Go
(`release.yml`, `setup-go` from `go.mod`), so a standard-library fix that the builder lacks would not be
reported for `vandoxd`. 0038 accepted this for the time between two Dependabot pull requests. Record 0041
accepts it until the next refresh by hand: a check before every release tag is documented. Automating it
is the follow-up issue (*Out of scope / follow-ups*).

## Acceptance criteria

- [ ] AC1: `go.mod` declares `go 1.27`, with no patch version and no `toolchain` directive.
  `golang.org/x/vuln` is at `v1.8.0`, the `tool golang.org/x/vuln/cmd/govulncheck` line is unchanged, and
  `go mod tidy` leaves `go.mod` and `go.sum` unchanged.
- [ ] AC2 (revised): `deploy/backend/Dockerfile` matches *Documentation updates* item 7 exactly. Before the
  first `FROM`, it declares six build arguments, each exactly once and with a default:
  - `BASE_BUILD_IMAGE="golang"`
  - `BASE_BUILD_TAG="1.27-trixie"`
  - `BASE_BUILD_DIGEST="sha256:3b77fc618ec235a1ab412de7737f120dd507c57e8d87de4cbb7994fb94275ed5"`
  - `BASE_RUNTIME_IMAGE="gcr.io/distroless/static-debian13"`
  - `BASE_RUNTIME_TAG="nonroot"`
  - `BASE_RUNTIME_DIGEST="sha256:e2e927ec666bae08560abb3c55d0659eceabb657f56b6782ab500a9fc7f555e3"`

  The digests are unchanged. The stages are `FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build` and
  `FROM ${BASE_RUNTIME_IMAGE}@${BASE_RUNTIME_DIGEST}`. The runtime stage re-declares the three
  `BASE_RUNTIME_*` arguments without defaults. Its `LABEL` adds
  `org.opencontainers.image.base.name="${BASE_RUNTIME_IMAGE}:${BASE_RUNTIME_TAG}"` and
  `org.opencontainers.image.base.digest="${BASE_RUNTIME_DIGEST}"`. Everything from `WORKDIR /src` to
  `ENTRYPOINT` is otherwise unchanged. `grep -nE '[a-z0-9]:[A-Za-z0-9_.-]+@sha256:' deploy/backend/Dockerfile`
  prints nothing: no line pairs a tag with a digest.
- [ ] AC3: `.squad/stack.md` *Toolchain* names Go `1.27`. *Known pitfalls* has the new local-govulncheck
  entry and the corrected golangci-lint entry, which no longer recommends a plain
  `go install …@<version>` (wording below). `go.mod`, the builder tag and `stack.md` all name 1.27.
- [ ] AC4: `.squad/project.md` row `/proc` no longer claims that `io/fs` has no link-reading interface
  (wording below).
- [ ] AC5: `docs/CONTRIBUTING.md` no longer contains the obsolete section *Before the first stable tag*
  (lines 142-146, inside the `releases` project block).
  `rg -n --hidden '1\.24' --glob '!specs/**' --glob '!.git/**'` finds only
  `.github/workflows/ci.yml:85` (correct, see above), `docs/decisions/0038-…` (accepted record, not
  rewritten) and record 0040, which names the old value. `--hidden` is required: without it `rg` skips
  `.squad/` and `.github/` (before the change it would miss `.squad/stack.md:9`, `.squad/project.md:149`
  and `ci.yml:85`).
- [ ] AC6: Locally under Go 1.27: *Format check*, *Build*, `go vet ./...`, *Test*, and golangci-lint
  v2.13.1 (*Analyzer gate*) are clean.
- [ ] AC7: In the pull request, CI is green: `build-test-lint`, `Vulnerability scan` (govulncheck against
  the live database), `SonarQube` and CodeQL. The release workflow's dry run is green, including *Check base
  image pinning*, *Check builder Go version*, *Build image* and *Verify image*. Its trigger paths include
  `go.mod`, `go.sum` and `deploy/backend/Dockerfile`, so the PR starts it.
- [ ] AC8 (revised): The diff contains no production or test code (`cmd/`, `internal/`, `*_test.go`).
  Among the workflow files, only `.github/workflows/release.yml` changes, and only as in AC9. Its *Build image*
  step is unchanged and passes only `VERSION`, `COMMIT` and `DATE` as build arguments, never a `BASE_*`
  argument.
- [ ] AC9: In `.github/workflows/release.yml`, *Check base image pinning* and *Check builder Go version*
  are replaced by the scripts in *Documentation updates* item 8, and the header comment names
  "records 0037, 0039 and 0041". The pinning check fails if any of the following holds:
  - a line starts with `from` or `arg` in any case, with or without leading white space, unless it is
    exactly upper-case `FROM`/`ARG` at the start of the line (e.g. `from alpine:latest AS evil`,
    `  FROM …`, `arg BASE_BUILD_IMAGE=…`, `  ARG BASE_BUILD_TAG=…`);
  - an `ARG` line declares more than one argument (e.g. `ARG X=1 BASE_BUILD_IMAGE=evil/golang
    BASE_BUILD_DIGEST=sha256:…`, or `ARG X=1 \` followed by a continuation line);
  - an `ARG` line ends with `\` or `` ` `` (a continuation, e.g. `ARG X=1\`);
  - the Dockerfile sets a `syntax` or `escape` parser directive;
  - the Dockerfile has no `FROM` line;
  - a `FROM` line is not `FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}` (optionally `AS <stage>`) with
    the same `<NAME>` twice;
  - a `BASE_<NAME>_IMAGE`, `_TAG` or `_DIGEST` is not declared exactly once, with a default, before the
    first `FROM` (a stage-local re-declaration with a default also fails);
  - a `_DIGEST` default is not `sha256:` plus 64 lower-case hex digits.

  The Go check fails if `BASE_BUILD_IMAGE` is not `golang`, if the build stage is not
  `FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build`, or if the `<major>.<minor>` at the start of
  `BASE_BUILD_TAG` differs from `go.mod`'s `go` directive. On the Dockerfile from AC2, both checks pass.
  No `${{ }}` expression appears inside either `run:` script.
- [ ] AC10: `.github/dependabot.yml` no longer has the `docker` entry: its comment and its `golang`
  `ignore` are gone too. The `github-actions` and `gomod` entries are unchanged.
- [ ] AC11: `docs/CONTRIBUTING.md` (inside the `releases` project block), `docs/ARCHITECTURE.md` and
  `.squad/project.md` match *Documentation updates* items 9-11. No current document claims that Dependabot
  updates the base image digests:
  `rg -n --hidden -i dependabot --glob '!specs/**' --glob '!.git/**' --glob '!docs/decisions/**'` finds
  only `.github/workflows/ci.yml:53, 55, 88` (Sonar token and gomod comments, unchanged),
  `.squad/routing.md:52` (unchanged) and the one sentence in `docs/CONTRIBUTING.md` *Base image digests*
  that says Dependabot cannot update these lines.
- [ ] AC12: Record 0041 matches what was built. At approval, the Lead sets 0041 to `Accepted`, sets 0036
  and 0038 to `Superseded by 0041` (the only edit to them), and updates the index.
- [ ] AC13: In the pull request, the SonarQube Cloud quality gate passes, with no `docker:S8431` on
  `deploy/backend/Dockerfile` and no new issue on the changed files. The release dry run is green with the
  new checks.

## Verification without tests

The change touches no production or test code, so steps 4 (*Skeleton*), 5 (*Tests first*) and the
*Coverage gate* of step 6 are **not applicable** (`.squad/routing.md`, *Changes without production or
test code*). The tier stays `security`: the plan challenge, step 3 and step 8 run in full. If production
or test code has to change after all (for example a new `go vet` finding), steps 4-6 apply again. I found
none under go1.27.0.

| AC | Verified where | By whom |
| -- | -------------- | ------- |
| AC1 | `head -5 go.mod`; `go mod tidy && git diff --exit-code go.mod go.sum`; `go list -m golang.org/x/vuln` → `v1.8.0` | Dev in step 6, then Code Officer in step 7; Reviewer reads the diff |
| AC2 | Read-only diff of the Dockerfile against item 7. The grep from AC2 prints nothing. Both digests equal the values pinned before this revision: `git show 3b2e482:deploy/backend/Dockerfile` (`3b77fc61…` at line 4, `e2e927ec…` at line 25). The builder digest was registry-verified in steps 3 and 8, so the revision does not re-pin it. If Security wants a fresh read: anonymous pull token, `Accept: application/vnd.oci.image.index.v1+json`, compare `docker-content-digest` for `library/golang:1.27-trixie` and `distroless/static-debian13:nonroot` on `gcr.io`. Also verified by the release dry run (*Build image*, *Verify image*, AC7/AC13). | Dev; Reviewer and Security (step 8); CI |
| AC3 | Read-only diff of `.squad/stack.md`; `grep -n '1\.27' go.mod deploy/backend/Dockerfile .squad/stack.md` | Reviewer (step 8) |
| AC4 | Read-only diff of `.squad/project.md` | Reviewer (step 8) |
| AC5 | Read-only diff of `docs/CONTRIBUTING.md`; the `rg` command from AC5 | Reviewer (step 8) |
| AC6 | *Format check*, *Build*, `go vet ./...`, *Test*, *Analyzer gate* with golangci-lint v2.13.1 on the PATH, built with Go 1.27: either the release tarball binary (built with go1.27.0) or `GOTOOLCHAIN=go1.27.1 go install github.com/golangci/golangci-lint/v2/cmd/golangci-lint@v2.13.1` — never a plain `go install`, which builds with go1.26 (see related defect 3). Confirm with `golangci-lint version`, which must name `go1.27.x`. Local `go tool govulncheck ./...` cannot reach vuln.go.dev in this sandbox, so state that plainly in the log; it is not a pass. | Code Officer (step 7) |
| AC7 | GitHub Actions on the PR: `CI` (all three jobs), `CodeQL`, `Release` (dry run, `v0.0.0-dryrun`) | Orchestrator reads the check results in step 11. A red check goes back through steps 7-8 |
| AC8 | `git diff --name-only origin/main...HEAD`. Allowed: `go.mod`, `go.sum`, `deploy/backend/Dockerfile`, `.github/workflows/release.yml`, `.github/dependabot.yml`, `.squad/stack.md`, `.squad/project.md`, `docs/CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, `docs/decisions/README.md`, `docs/decisions/0036-…` and `0038-…` (status line only, at approval), `docs/decisions/0040-…`, `docs/decisions/0041-…`, `specs/issue-103/*`. `git diff origin/main...HEAD -- .github/workflows/release.yml` touches only the header comment and the two check steps. | Reviewer (step 8) |
| AC9 | Read-only diff of `release.yml` against item 8. Optionally run the two scripts locally with `bash` from the repository root: both must print nothing and exit 0. The Lead checked the scripts (revision after Security round 2) in a scratch copy of the AC2 Dockerfile, built from item 7's text: both pass there (exit 0, no output), and each of these fourteen cases exits 1 with an `::error::`: (1) appended `from alpine:latest AS evil`; (2) appended `  FROM alpine:latest AS evil`; (3) `ARG X=1 BASE_BUILD_IMAGE=evil/golang BASE_BUILD_DIGEST=sha256:000…` after the global ARGs; (4) `arg BASE_BUILD_IMAGE=evil/golang`; (5) `  ARG BASE_BUILD_TAG=1.26-trixie`; (6) `ARG X=1 \` plus a continuation line `    BASE_BUILD_DIGEST=sha256:000…`; (7) the same with `ARG X=1\` (no space); (8) `# syntax=evil/frontend:latest` as line 1; (9) ``# escape=` `` as line 1; (10) a literal `FROM` with a tag; (11) a non-sha256 digest default; (12) a stage-local ARG with a default; (13) mismatched `<NAME>`s; (14) a builder tag `1.26-trixie` and `BASE_BUILD_IMAGE` other than `golang` (Go check, one case each). With only Security's two guards, cases 7-9 still pass, which is why the Lead added the last two guards. The Code Officer repeats at least the good case and cases 1-6 in step 7. Finally verified by the release dry run (AC13). | Code Officer runs the scripts in step 7; Reviewer and Security (step 8); CI |
| AC10 | Read-only diff of `.github/dependabot.yml` | Reviewer (step 8) |
| AC11 | Read-only diff of the three files; the `rg` command from AC11 | Reviewer (step 8) |
| AC12 | Read-only comparison of record 0041 with the diff; status and index edits | Lead (step 9) |
| AC13 | GitHub checks on the PR after the push: the SonarQube Cloud quality gate and the Sonar issue list for `deploy/backend/Dockerfile`, and the `Release` dry run. Sonar has no local equivalent (`.squad/stack.md`), so the grep in AC2 is the only local proxy. A remaining or new Sonar finding goes back through steps 6-8. | Orchestrator (step 11) |

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
| backend image | `deploy/backend/Dockerfile` | Revised: base-image build arguments and digest-only `FROM` lines (item 7). The builder digest `3b77fc61…` (index digest, Go 1.27.1, image created 2026-09-19; verified: sha256 of the raw index body equals the `docker-content-digest` header) and the runtime digest `e2e927ec…` are kept unchanged |
| release | `.github/workflows/release.yml:6, 142-169` | Header comment and the two check steps (item 8) |
| Dependabot | `.github/dependabot.yml:23-38` | Remove the `docker` entry (AC10) |
| docs | `docs/CONTRIBUTING.md`, `docs/ARCHITECTURE.md` | Items 9 and 10 |
| squad stack (product description) | `.squad/stack.md` | See *Documentation updates* |
| project knowledge | `.squad/project.md:149` | See *Documentation updates* |
| docs | `docs/CONTRIBUTING.md:142-146` | Remove the section; see *Documentation updates* |
| CI | `.github/workflows/ci.yml`, `codeql.yml` | **No change.** Every `setup-go` reads `go-version-file: go.mod`. The comment at `ci.yml:85` is correct as written. golangci-lint v2.13.1 is built with go1.27.0 |

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
3. `.squad/stack.md`, *Known pitfalls*, lines 75-78 (the golangci-lint bullet): replace the whole bullet with
   `` - `golangci-lint` must be on the PATH; the version in CI (`.github/workflows/ci.yml`) is the reference. It refuses to run ("the Go language version used to build golangci-lint is lower than the targeted Go version") when it was built with an older Go than `go.mod` targets. A plain `go install github.com/golangci/golangci-lint/v2/cmd/golangci-lint@<version>` does not avoid this: with `GOTOOLCHAIN=auto` it builds with the Go version from golangci-lint's own `go.mod`, which can be older than this module's. Use the binary from the golangci-lint GitHub release, or force a toolchain at least as new as `go.mod` (`GOTOOLCHAIN=go1.27.<n> go install …@<version>`); `golangci-lint version` shows the Go it was built with. ``
   (wrapped at the file's usual line width).
4. `.squad/project.md:149`, row `/proc`: replace
   `` symlink targets such as `/proc/<pid>/fd/*` through a link-reading method on the same small interface, since Go 1.24's `io/fs` has none `` with
   `` symlink targets such as `/proc/<pid>/fd/*` through `io/fs.ReadLinkFS` (Go 1.25 and later; implemented by `os.DirFS` and `fstest.MapFS`, so the blocking wrapper implements it too) ``.
   The rest of the row stays.
5. `docs/CONTRIBUTING.md`: delete lines 142-146 (the heading `### Before the first stable tag` at 142,
   the blank line at 143, the two-line paragraph at 144-145 and the blank line at 146), so that line 141
   (blank) is followed directly by `### Re-running a failed release`. This change fulfils that requirement. Keep the
   `<!-- project:… releases -->` markers intact.
6. `README.md`: no change. It names no Go version, and its line 72 ("distroless static base") stays true.
   ARCHITECTURE.md:223 ("only after `govulncheck` passes") stays true. ARCHITECTURE.md changes only as
   in item 10.

Revision after the PR (SonarQube `docker:S8431`, record 0041). The Dev makes items 7-11:

7. `deploy/backend/Dockerfile`: lines 1-4 become the block below, followed by the unchanged lines from
   the old line 5 (blank) to the old line 24 (blank):

   ```dockerfile
   # Backend image for vandoxd. The build context is the repository root:
   #   docker build -f deploy/backend/Dockerfile .
   # Base images are pinned by digest only; each tag is kept in its own ARG for readability and the
   # OCI base-image labels. The builder tag follows the `go` directive in go.mod. The release workflow
   # checks both; the digests are refreshed by hand (docs/CONTRIBUTING.md, record 0041).
   ARG BASE_BUILD_IMAGE="golang"
   ARG BASE_BUILD_TAG="1.27-trixie"
   ARG BASE_BUILD_DIGEST="sha256:3b77fc618ec235a1ab412de7737f120dd507c57e8d87de4cbb7994fb94275ed5"
   ARG BASE_RUNTIME_IMAGE="gcr.io/distroless/static-debian13"
   ARG BASE_RUNTIME_TAG="nonroot"
   ARG BASE_RUNTIME_DIGEST="sha256:e2e927ec666bae08560abb3c55d0659eceabb657f56b6782ab500a9fc7f555e3"

   FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build
   ```

   The old runtime stage (old lines 25-36) becomes:

   ```dockerfile
   FROM ${BASE_RUNTIME_IMAGE}@${BASE_RUNTIME_DIGEST}

   ARG BASE_RUNTIME_IMAGE
   ARG BASE_RUNTIME_TAG
   ARG BASE_RUNTIME_DIGEST
   ARG VERSION=dev
   ARG COMMIT=unknown
   ARG DATE=unknown

   LABEL org.opencontainers.image.title="vandox" \
         org.opencontainers.image.source="https://github.com/LarsLaskowski/Vandox" \
         org.opencontainers.image.licenses="MIT" \
         org.opencontainers.image.version="${VERSION}" \
         org.opencontainers.image.revision="${COMMIT}" \
         org.opencontainers.image.created="${DATE}" \
         org.opencontainers.image.base.name="${BASE_RUNTIME_IMAGE}:${BASE_RUNTIME_TAG}" \
         org.opencontainers.image.base.digest="${BASE_RUNTIME_DIGEST}"
   ```

   Old lines 37-42 (`COPY --from=build`, `USER 65532:65532`, `ENTRYPOINT`) stay unchanged.
8. `.github/workflows/release.yml`:
   - Line 6: `records 0037 to 0039.` → `records 0037, 0039 and 0041.`
   - The `run:` block of *Check base image pinning* (lines 143-154) becomes the script below. It keeps the
     step name and the indentation of the surrounding steps.

     ```bash
     set -euo pipefail
     f=deploy/backend/Dockerfile
     if grep -nEi '^[[:space:]]*(from|arg)([[:space:]]|$)' "$f" | grep -vE '^[0-9]+:(FROM|ARG)[[:space:]]'; then
       echo "::error::FROM and ARG must be upper-case and start at the beginning of the line in $f"
       exit 1
     fi
     if grep -nE '^ARG[[:space:]]+[^[:space:]]+[[:space:]]+[^[:space:]]' "$f"; then
       echo "::error::each ARG line in $f must declare exactly one argument"
       exit 1
     fi
     if grep -nE '^ARG.*[\\`][[:space:]]*$' "$f"; then
       echo "::error::an ARG line in $f must not continue onto the next line"
       exit 1
     fi
     if grep -nEi '^#[[:space:]]*(syntax|escape)[[:space:]]*=' "$f"; then
       echo "::error::$f must not set the syntax or escape parser directive"
       exit 1
     fi
     first_from="$(grep -nE '^FROM[[:space:]]' "$f" | head -n1 | cut -d: -f1)"
     if [ -z "$first_from" ]; then
       echo "::error::no FROM line in $f"
       exit 1
     fi
     # Prints the default of a global ARG. Fails unless it is declared exactly once in the
     # file, with a default, before the first FROM.
     arg_default() {
       local name="$1" all before
       all="$(grep -cE "^ARG[[:space:]]+${name}=" "$f" || true)"
       before="$(head -n "$((first_from - 1))" "$f" | grep -cE "^ARG[[:space:]]+${name}=" || true)"
       if [ "$all" != "1" ] || [ "$before" != "1" ]; then
         return 1
       fi
       sed -nE "s/^ARG[[:space:]]+${name}=\"?([^\"[:space:]]+)\"?[[:space:]]*$/\1/p" "$f"
     }
     re='^FROM[[:space:]]+\$\{(BASE_[A-Z]+)_IMAGE\}@\$\{(BASE_[A-Z]+)_DIGEST\}([[:space:]]+AS[[:space:]]+[a-z][a-z0-9_-]*)?[[:space:]]*$'
     bad=0
     while IFS= read -r line; do
       if [[ ! "$line" =~ $re ]] || [ "${BASH_REMATCH[1]}" != "${BASH_REMATCH[2]}" ]; then
         echo "::error::FROM line must be FROM \${BASE_<NAME>_IMAGE}@\${BASE_<NAME>_DIGEST}: $line"
         bad=1
         continue
       fi
       p="${BASH_REMATCH[1]}"
       if ! digest="$(arg_default "${p}_DIGEST")" || ! printf '%s' "$digest" | grep -Eq '^sha256:[0-9a-f]{64}$'; then
         echo "::error::${p}_DIGEST must be declared once before the first FROM with a sha256 digest default"
         bad=1
       fi
       for kind in IMAGE TAG; do
         if ! val="$(arg_default "${p}_${kind}")" || [ -z "$val" ]; then
           echo "::error::${p}_${kind} must be declared once before the first FROM with a non-empty default"
           bad=1
         fi
       done
     done < <(grep -E '^FROM[[:space:]]' "$f")
     if [ "$bad" -ne 0 ]; then
       exit 1
     fi
     ```

   - The `run:` block of *Check builder Go version* (lines 157-169) becomes:

     ```bash
     set -euo pipefail
     f=deploy/backend/Dockerfile
     mod="$(awk '$1=="go"{print $2; exit}' go.mod | cut -d. -f1,2)"
     name="$(sed -nE 's/^ARG[[:space:]]+BASE_BUILD_IMAGE="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
     tag="$(sed -nE 's/^ARG[[:space:]]+BASE_BUILD_TAG="?([^"[:space:]]+)"?[[:space:]]*$/\1/p' "$f")"
     img="$(printf '%s' "$tag" | sed -nE 's/^([0-9]+\.[0-9]+)([.-].*)?$/\1/p')"
     if [ "$name" != "golang" ] || ! grep -Eq '^FROM[[:space:]]+\$\{BASE_BUILD_IMAGE\}@\$\{BASE_BUILD_DIGEST\}[[:space:]]+AS[[:space:]]+build[[:space:]]*$' "$f"; then
       echo "::error::the build stage must be FROM \${BASE_BUILD_IMAGE}@\${BASE_BUILD_DIGEST} AS build with BASE_BUILD_IMAGE golang (got '$name')"
       exit 1
     fi
     if ! printf '%s' "$mod" | grep -Eq '^[0-9]+\.[0-9]+$' || ! printf '%s' "$img" | grep -Eq '^[0-9]+\.[0-9]+$'; then
       echo "::error::could not read Go versions (go.mod: '$mod', BASE_BUILD_TAG: '$tag')"
       exit 1
     fi
     if [ "$mod" != "$img" ]; then
       echo "::error::builder image Go $img (BASE_BUILD_TAG '$tag') differs from go.mod Go $mod"
       exit 1
     fi
     ```

     *Check base image pinning* runs first, so the `BASE_BUILD_*` declarations are already known to be
     unique, upper-case, unindented and one per line by the time this step reads them.

     The four guards at the top of the pinning script come from the Security plan review (round 2, B1)
     and close bypasses of the line-anchored, upper-case `grep`s below: a lower-case or indented `FROM`
     that the `^FROM` loop never sees, a lower-case or indented `ARG` that re-declares a default unseen,
     an `ARG` line that declares several names (Docker accepts `ARG A=1 B=2`), an `ARG` line continued
     onto the next line with `\` (or with `` ` `` after an `escape` directive), and a `# syntax=` directive,
     which would let an unpinned frontend image run the whole build. The first two guards are Security's
     text verbatim; the last two are the Lead's additions for the same class.
   - The step order stays the same, and nothing else in the file changes, in particular *Build image* (AC8).
9. `docs/CONTRIBUTING.md` (all inside the `releases` project block):
   - Line 81: `[0038](decisions/0038-backend-image-distroless-nonroot-pinned-by-digest.md) and` →
     `[0041](decisions/0041-base-images-pinned-by-digest-through-build-arguments.md) and`. The sentence then
     names 0037, 0041 and 0039.
   - *Cutting a release*: before `On an up-to-date \`main\`:`, insert the paragraph
     `` Before tagging, check that the base image digests are current (*Base image digests* below). ``
     followed by a blank line.
   - *What the release workflow does*, item 4: replace `that every \`FROM\` is pinned by digest, that the
     builder's Go minor version equals \`go.mod\`'s` with `that every \`FROM\` uses a base image build
     argument pinned by a sha256 digest, that the builder tag's Go minor version equals \`go.mod\`'s`.
   - New subsection directly before `### Dry run on pull requests`:

     ```markdown
     ### Base image digests

     `deploy/backend/Dockerfile` names each base image in three build arguments: `BASE_<NAME>_IMAGE`,
     `BASE_<NAME>_TAG` and `BASE_<NAME>_DIGEST`. `FROM` uses only the image and the digest. Dependabot cannot
     read these lines, so the digests are refreshed by hand: before every release tag, and whenever a Go
     patch release or a distroless update appears. Read the multi-arch index digest of exactly the tag in
     `BASE_<NAME>_TAG`, for example `docker buildx imagetools inspect golang:1.27-trixie` (the `Digest:`
     line), and write it to `BASE_<NAME>_DIGEST` in a pull request. A new Go minor version changes the
     `go` line in `go.mod`, `BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` together. The release build passes no
     `BASE_*` build argument, so the pinned defaults are what it uses.
     ```
10. `docs/ARCHITECTURE.md` (inside the `architecture` project block):
    - Lines 218-219: replace
      `` - The image is built from `deploy/backend/Dockerfile` on a distroless static base pinned by digest and runs as UID 65532. `` with
      `` - The image is built from `deploy/backend/Dockerfile` on a distroless static base and runs as UID 65532. The builder and runtime base images are pinned by digest: each `FROM` names an image and a digest from build arguments, and the tag is kept in a separate build argument and in the image's OCI base-image labels. The release build sets none of these arguments, and the digests are refreshed by hand. ``
      (wrapped at the file's usual width).
    - Line 227: `[0038](decisions/0038-backend-image-distroless-nonroot-pinned-by-digest.md),` →
      `[0041](decisions/0041-base-images-pinned-by-digest-through-build-arguments.md),`.
11. `.squad/project.md`, security area 13:
    - Line 83: `` Every action is pinned by commit SHA and every base image by digest. `` →
      `` Every action is pinned by commit SHA and every base image by digest (`FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}` with build-argument defaults that the release build never overrides; the release workflow checks the form). ``
    - Line 90: `Records 0027, 0037, 0038, 0039.` → `Records 0027, 0037, 0039, 0041.`

    A security area's wording changes here. The goal ("every base image by digest") does not change.

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is weakened:

- **Release built from source, without caches, only after govulncheck passes** (0037). This is preserved
  and, in fact, kept working: with the tool raised, govulncheck can still analyse code under Go 1.27.
- **Base images pinned by digest; builder minor follows `go.mod`** (0038, superseded by 0041). This is
  preserved. Both stages pull by digest only (`image@sha256:…` from build-argument defaults), and the
  release check now validates the arguments: it is at least as strict as the old `@sha256:` grep, because
  it also rejects stage-local overrides and mismatched name pairs. The minor is read from
  `BASE_BUILD_TAG`, and the check enforces 1.27 = 1.27.
- **What changes (accepted by the Product Manager's decision):** Dependabot no longer refreshes the
  digests (0036 and 0038's "Dependabot updates the digests"). No guarantee in `docs/ARCHITECTURE.md` or
  in *Guarantees* of `.squad/project.md` names Dependabot, so no listed guarantee is weakened. The
  maintenance process moves to the documented refresh by hand (item 9) and a follow-up issue.
- **Both binaries share the Go minor version** (0038 consequence). Preserved.
- The binaries' behavior does not change: no production code changes, and tests pass unchanged under
  go1.27.0.

## Security considerations

- **Supply chain.** The builder digest comes from Docker Hub's official `library/golang` repository. I
  verified it by hashing the raw index body against `docker-content-digest`. Security should re-read it
  independently in step 8. The x/vuln update brings new `golang.org/x/*` versions (official Go
  sub-repositories) and five test-only modules in `go.sum` (`google/go-cmdtest`, `go-cmp`, `renameio`,
  and the deprecated `golang.org/x/tools/go/expect` and `golang.org/x/tools/go/packages/packagestest`).
  None of them is linked into either binary: they are dependencies of the `tool` only, and
  `go version -m` on the binaries should show no `golang.org/x` modules.
- **Vulnerability gate stays effective.** Without the tool update, govulncheck panics under 1.27 when a
  reachable vulnerability exists. That would turn the release gate into a hard failure exactly when it
  matters (a fail-closed outage, not a bypass).
- **Patch currency.** The `go` line stays without a patch version, so `setup-go` keeps taking the newest
  1.27.x and govulncheck checks against current standard-library fixes. *Revised:* the builder image
  moves only through digest refreshes by hand (0041; previously Dependabot, 0038). The builder patch can
  therefore lag longer, and the source-mode `govulncheck` does not see the builder's standard library.
  This is accepted in 0041, mitigated by the check before every release tag (item 9), and covered by the
  follow-up issue.
- **Build-argument override (revision).** With ARG-based `FROM`, `docker build --build-arg
  BASE_RUNTIME_DIGEST=…` can swap a base image. Only the release workflow builds published images. Its
  *Build image* step passes only `VERSION`, `COMMIT` and `DATE` (AC8), and its checks pin the form of
  every `FROM` and every default (AC9). A local or third-party rebuild with overridden arguments is not a
  published artifact.
- **Check robustness (revision).** The new checks fail closed: a missing `FROM`, an unparseable
  argument, a duplicate or stage-local default, or an unreadable minor all produce an `::error::` and
  exit 1. They contain no `${{ }}` expression, and they read only the checked-out repository.
- **OCI labels.** `base.name` and `base.digest` name the public base image only. They expose nothing that
  is not already in the public Dockerfile.
- No secret, permission or workflow trigger changes. The runtime image content is unchanged, and so are
  both digests.

## Decision records

- `docs/decisions/0040-go-1-27-toolchain-and-govulncheck-v1-8.md` (Proposed): the target minor (1.27
  over 1.26), the patch-less `go` directive, and govulncheck raised to v1.8.0. Records 0037 and 0038 are
  neither superseded nor edited: their rules hold. A search for `1\.24` finds this record and 0038 (the
  historical value), plus `ci.yml:85`. *Revision:* 0040 was accepted in step 9 and stays as written. Its
  sentence "Records 0037 and 0038 stay as they are" and its literal `golang:1.27-trixie@sha256:…` were
  true when it was accepted. 0041 records the new form, and the digest value is the same.
- `docs/decisions/0041-base-images-pinned-by-digest-through-build-arguments.md` (Proposed, revision):
  - the ARG-based digest-only pinning for both stages, with OCI base labels;
  - the adapted release checks;
  - removing the Dependabot `docker` entry;
  - refreshing the digests by hand.

  It supersedes **0036** (the Dependabot `docker` entry) and **0038** (pinning form and Dependabot digest
  updates). It carries over 0038's other decisions (runtime base, tags, `latest`, never-overwrite)
  unchanged. At approval (step 9), the Lead sets 0041 to `Accepted` and 0036 and 0038 to
  `Superseded by 0041`, the only edit allowed to them. The Lead also updates both index rows and adds 0041
  to the index in `docs/decisions/README.md`. Until then, 0036 and 0038 stay `Accepted`, because a
  `Proposed` record does not yet supersede anything. **0037** was checked: it mentions Dependabot only for
  action SHAs and digests only for the published image, so it is unaffected. **0039** line 76 cites 0038
  for "never overwritten", which 0041 carries over. Accepted records are not edited, so it stays.

## Challenge

Devil's Advocate verdict: OBJECTIONS, 0 major, 2 minor. Both accepted.

1. **minor — AC5's `rg` skips hidden directories.** Accepted, confirmed: `rg -n '1\.24' --glob '!specs/**'`
   today misses `.squad/stack.md:9`, `.squad/project.md:149` and `.github/workflows/ci.yml:85`, so AC5
   could pass with the two `.squad/` lines left stale. AC5 now uses
   `rg -n --hidden '1\.24' --glob '!specs/**' --glob '!.git/**'` and says why `--hidden` is required. The
   expected hits are unchanged (`ci.yml:85`, 0038, 0040), and they now include the hidden file they name.
2. **minor — AC6's `go install …@v2.13.1` produces a go1.26 build that refuses the go 1.27 module.**
   Accepted, confirmed: golangci-lint v2.13.1's `go.mod` declares `go 1.26.0` with no `toolchain` line, so
   `GOTOOLCHAIN=auto` builds it with a go1.26 toolchain. The AC6 row now requires the release tarball
   binary or `GOTOOLCHAIN=go1.27.1 go install …@v2.13.1`, checked with `golangci-lint version`. Related
   defect 3 explains the trap. *Documentation updates* item 3 rewrites the golangci-lint pitfall at
   `.squad/stack.md:75-78`, and AC3 checks it. Record 0040 (*Consequences*) no longer says "v2.13.1 works"
   without qualification.

### Revision after the PR: SonarQube Cloud `docker:S8431` and the Product Manager's decision

Input: the quality gate failed on `deploy/backend/Dockerfile:4` (new code) and `:25` (older), with the
finding "use either the version tag or the digest, not both". The Product Manager decided to follow the
pattern of the other repositories (PlexToJellyfinSync), even if 0038 must change.

- **Accepted, plan revised.** Both `FROM` lines follow PlexToJellyfinSync's runtime pattern (item 7). The
  builder is pinned the same way rather than copying PlexToJellyfinSync's tag-only builder, because 0038
  pins it and the Product Manager's decision keeps digest pinning. The release checks are rewritten to
  read the arguments (item 8; validated in a scratch copy against six negative cases).
  `.github/dependabot.yml` loses its now-inert `docker` entry (AC10). The docs and `.squad/project.md`
  follow (items 9-11). Record 0041 supersedes 0036 and 0038.
- **Not followed: PlexToJellyfinSync keeps a `docker` Dependabot entry.** Dependabot's parser cannot
  match `FROM ${…}`, so that entry updates nothing. Keeping it here would leave a comment ("digest
  updates of the same tag continue") and an `ignore` rule that are false. The consequence for issue #8's
  "Dependabot covers docker" is stated in 0041.
- **Scope kept minimal.** An automated digest refresh, or a release-time `govulncheck -mode=binary` on
  the built `vandoxd`, is not added. For a binary stripped with `-s -w`, x/vuln v1.8.0
  (`internal/vulncheck/binary.go:107-111`) falls back to module-level precision, so every
  standard-library vulnerability of the builder's patch would block a release. That is a new release
  policy, and it goes into the follow-up issue.
- Tier stays `security`. The revision touches Docker, CI and Dependabot configuration and the wording of
  security area 13. No production or test code changes, so steps 4-5 and the *Coverage gate* remain not
  applicable. Steps 3, 6, 7 and 8 run again for the revision: a delta round with Reviewer and Security.

## Out of scope / follow-ups

- `actions/setup-go` prefers a cached 1.27.x on the runner over the newest patch (`check-latest` is not
  set). 0038's consequence "setup-go takes the latest patch release" is therefore slightly optimistic. This
  behavior predates this change, and govulncheck would flag a vulnerable cached patch. No follow-up is
  proposed unless the Devil's Advocate or Security asks for one.
- **Follow-up issue (revision), for the orchestrator to create:**
  - Title: `[deploy] Automate the base image digest refresh for deploy/backend/Dockerfile`
  - Body: "Record 0041 pins both base images of `deploy/backend/Dockerfile` through
    `BASE_<NAME>_IMAGE`, `_TAG` and `_DIGEST` build arguments with `FROM ${…_IMAGE}@${…_DIGEST}`. This
    pattern satisfies SonarQube's `docker:S8431`. Dependabot's `docker` parser cannot read these lines,
    so the `docker` entry was removed, and the digests are now refreshed by hand (`docs/CONTRIBUTING.md`,
    *Base image digests*). Between two refreshes, the builder's Go patch can lag behind the runner's, and
    the source-mode release `govulncheck` does not see that. Options:
    (a) a scheduled workflow that resolves each `BASE_<NAME>_TAG` to its current multi-arch index digest
    and opens an issue or pull request when it differs from `BASE_<NAME>_DIGEST`;
    (b) Renovate with a custom regex manager for the ARG triplets;
    (c) a release-time `govulncheck -mode=binary` on the built `vandoxd`. Because the binary is built with
    `-s -w`, this check works at module level and blocks on any standard-library vulnerability of the
    builder's patch.
    Decide on one option with a decision record that extends 0041."
- No other follow-up issue.
