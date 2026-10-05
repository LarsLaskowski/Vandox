# Plan: Report stale base image digests weekly and check the builder's Go version in the build

Source: Issue #108
Status: Draft
Tier: security — the change adds a workflow with a write permission, changes `ci.yml` and `deploy/backend/Dockerfile` (Docker/CI configuration, security area 13 *Release pipeline and published artifacts*).

## Problem / root cause

Record 0041 (option 6) chose to refresh the base image digests of `deploy/backend/Dockerfile` by hand and
left automation to this issue. Nothing reminds anyone that a refresh is due, and nothing checks that
`BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` belong together.

Claims of the issue, checked:

- *0041 pins both base images through `BASE_<NAME>_IMAGE/_TAG/_DIGEST` with `FROM ${…_IMAGE}@${…_DIGEST}`*:
  confirmed, `deploy/backend/Dockerfile` lines 6–11, 13 and 37.
- *Dependabot cannot read these lines, so the `docker` entry was removed*: confirmed, `.github/dependabot.yml`
  has only `github-actions` and `gomod`; the parser limitation is documented in 0041 (checked there).
- *Digests are refreshed by hand per `docs/CONTRIBUTING.md`, Base image digests*: confirmed, lines 132–141,
  and line 90 (check before tagging).
- *The builder's Go patch can lag the runner's, and source-mode release `govulncheck` does not see that*:
  confirmed, `.github/workflows/release.yml` runs `go tool govulncheck ./...` after `setup-go` with
  `go-version-file: go.mod` (newest 1.27.x), not with the builder image's Go.
- *Option (c): with `-s -w`, binary-mode govulncheck works at module level and blocks on any stdlib
  vulnerability of the builder's patch*: confirmed in govulncheck v1.8.0 (the version `go.mod` pins),
  `internal/buildinfo/additions_scan.go` lines 86–90 (stripped binary: module info only) and
  `internal/vulncheck/binary.go` lines 107–111 (stripped: "go.mod-level precision", all known vulnerable
  symbols of every affected module).
- *Release checks cannot verify statically that `BASE_BUILD_TAG` and `BASE_BUILD_DIGEST` belong together*:
  confirmed, `.github/scripts/check-base-image-pinning.sh` and `.github/scripts/check-builder-go-version.sh`
  read only the Dockerfile text.
- *`RUN go version` is a guard*: refuted as stated. It only prints and never fails the build; the
  `go env GOVERSION` comparison the issue names as the alternative is what is planned.
- *PR #107 merged first*: confirmed, record 0041 is `Accepted`.

Exercised at plan time: `docker buildx imagetools inspect --format '{{json .Manifest}}'
gcr.io/distroless/static-debian13:nonroot` returned the OCI index whose `digest` equals the pinned
`BASE_RUNTIME_DIGEST` (current today). The same lookup for `golang:1.27-trixie` failed with
`429 Too Many Requests` from Docker Hub, so a failed lookup must be an error, never "stale" or "current".
The in-build guard below was run under `dash` with a stubbed `go` for every tag form listed in *Accepted
forms*; results as stated there.

Related defect found on the way: none.

## Acceptance criteria

- [ ] AC1: Record `docs/decisions/0055-stale-base-image-digests-reported-weekly-builder-go-checked-in-build.md`
  exists (`Proposed`, extends 0041, 0041 stays `Accepted`), chooses option (a) in the *issue* variant plus
  the in-build guard, and states why (b), (c), the PR and fail-only variants of (a), and a release or PR
  gate on freshness were rejected.
- [ ] AC2: `.github/scripts/check-base-image-digests.sh` exists, mode `100755`, `#!/usr/bin/env bash`,
  `set -euo pipefail`, run from the repository root with no arguments, and:
  - a. runs `.github/scripts/check-base-image-pinning.sh` first; if that fails, exits 1;
  - b. takes every `<NAME>` from the `FROM` lines (`^FROM[[:space:]]+\$\{(BASE_[A-Z]+)_IMAGE\}@…`), not a
    fixed list, and reads the `_IMAGE`, `_TAG` and `_DIGEST` defaults;
  - c. exits 1 with `::error::` unless the image matches `^[a-z0-9][a-z0-9._/-]*$`, the tag matches
    `^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$` and the pinned digest matches `^sha256:[0-9a-f]{64}$`, before
    any of them is passed to a command;
  - d. resolves `<image>:<tag>` with `docker buildx imagetools inspect --format '{{json .Manifest}}'` and
    `jq -r .digest`; a non-zero exit of the lookup (e.g. HTTP 429) or a result that does not match
    `^sha256:[0-9a-f]{64}$` is an error (`::error::`, exit 1), never a stale or current result;
  - e. prints to standard output a Markdown table `| Base | Image | Pinned digest | Current digest | State |`
    with one row per base (`BASE_<NAME>`, `<image>:<tag>`, the two digests, `current` or `stale`);
  - f. for each stale base prints `::warning::BASE_<NAME>_DIGEST is stale: <image>:<tag> is now <current>, pinned <pinned>`
    to standard error;
  - g. exits 0 when every digest is current, 3 when at least one is stale and no error occurred, 1 on any
    error (an error wins over stale; every base is still looked up so the output is complete).
- [ ] AC3: `.github/workflows/base-image-digests.yml` exists with:
  - a. triggers exactly `schedule` (`cron: "0 5 * * 1"`) and `workflow_dispatch`; no `push`,
    `pull_request` or other trigger;
  - b. top-level `permissions: {}`; one job with exactly `contents: read` and `issues: write`;
    `concurrency` group `base-image-digests`, `cancel-in-progress: false`;
  - c. `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1` with
    `persist-credentials: false`; no other action;
  - d. no `${{ }}` expression inside any `run:` script; `GH_TOKEN: ${{ github.token }}` and
    `GH_REPO: ${{ github.repository }}` only through `env:`;
  - e. script status 0: logs "current" and succeeds without any GitHub write; status 3: looks up open
    issues through `gh api` (REST, `--paginate`, pull requests filtered out) with the exact title
    `Base image digests are stale`; if one exists, `PATCH`es its body; otherwise creates it with the labels
    `dependencies` and `area: docker`; any other status: fails the job without writing;
  - f. the issue body is the script's table plus fixed text pointing to `docs/CONTRIBUTING.md`,
    *Base image digests*; no other variable content;
  - g. writes no file into the repository, pushes nothing, opens no pull request, closes no issue.
- [ ] AC4: In `.github/workflows/ci.yml`, job `release-build`, a step `Check base image digests` follows
  `Check builder Go version`: it runs the script; status 0 and 3 pass (3 leaves the warning annotations),
  any other status fails the job. No other change to `ci.yml`.
- [ ] AC5: In `deploy/backend/Dockerfile`, the build stage, directly after `WORKDIR /src` and before
  `COPY go.mod go.sum ./`, contains a bare `ARG BASE_BUILD_TAG` and one `RUN` that fails the build unless
  `go env GOVERSION` equals `go<v>` or starts with `go<v>.`, where `<v>` is `${BASE_BUILD_TAG%%-*}`, with
  the pattern parts quoted so they match literally (exact text under *Approach*). The header comment says
  how stale digests are reported (record 0055). Both existing check scripts still pass unchanged against
  the new Dockerfile; no other Dockerfile line changes.
- [ ] AC6: `release.yml`, `.github/dependabot.yml` and both existing scripts in `.github/scripts/` are
  unchanged.
- [ ] AC7: Documentation updated as listed under *Documentation updates*. Outside `docs/decisions/`, every
  hit of `grep -rn "by hand" docs/ARCHITECTURE.md docs/CONTRIBUTING.md deploy/backend/Dockerfile` that is
  about base image digests also names the weekly check (record 0055); the other hits (e.g. CONTRIBUTING
  "Do not push by hand") are unrelated and unchanged.

## Verification without tests

The change touches no production or test code (only a Dockerfile, a shell script, two workflows and
Markdown). Steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* of step 6 are **not
applicable** (`.squad/routing.md`, *Changes without production or test code*). The tier stays `security`:
plan challenge, steps 3 and 8 and step 7 (*Format check*, *Analyzer gate*) still run.

| AC | Where verified | By whom |
| -- | -------------- | ------- |
| AC1 | Reading the record against the diff | Reviewer (step 8), Lead (step 9) |
| AC2 a–g | Dev runs the script in the repository root and in a scratch `git worktree` with (1) the committed Dockerfile, (2) one `_DIGEST` default changed to another valid sha256 (expect 3, warning, table row `stale`), (3) a tag default set to `-x` and to `'nonroot'` (expect 1 before any lookup), (4) a FROM line broken (expect 1 from the pinning check), (5) an unresolvable tag such as `nonroot-doesnotexist` (expect 1); reports each exit status and output. A Docker Hub 429 during a run is itself the error-path check for `golang`; the `gcr.io` runtime base resolves from this session. `bash -n` on the script; `shellcheck` if available. Then the PR's *Release build check* run on a GitHub runner (AC4) shows the real result for both bases | Dev (step 6), Reviewer and Security re-run (1)–(3) read-only in a scratch worktree (step 8) |
| AC3 a–d, f, g | Read-only review of the workflow file; `grep -n '\${{' .github/workflows/base-image-digests.yml` shows hits only in `env:` lines | Reviewer, Security (step 8) |
| AC3 e | Cannot run before merge (a `workflow_dispatch` needs the file on `main`). The status handling is a `case` on the script status reviewed in step 8; after the merge the maintainer runs the workflow once by `workflow_dispatch` and confirms a green run with no issue (digests current) — recorded as a post-merge check in the PR's *Next steps* | Reviewer, Security (step 8); maintainer after merge |
| AC4 | The PR's CI run: `Release build check` contains the new step, green, with a warning only if a digest is stale | Orchestrator reads the run (step 11), Reviewer (diff) |
| AC5 | The PR's `Release build check` builds the image (guard passes with the pinned builder). The guard command is run under `sh` with a stub `go` for the cases in *Accepted forms* (as done at plan time); a local `docker build --build-arg BASE_BUILD_TAG=1.26-trixie` (expect failure at the guard) if Docker Hub allows the pull. Both existing scripts run against the new Dockerfile and pass | Dev (step 6), Security (step 8) |
| AC6, AC7 | `git diff --stat origin/main...HEAD` and the `grep` in AC7 | Reviewer (step 8), Lead (step 9) |

## Approach

1. **Script** `.github/scripts/check-base-image-digests.sh` (AC2). Structure follows the two existing
   scripts (same header comment style, `f=deploy/backend/Dockerfile`). Reads defaults with the same pattern
   the existing scripts use, `sed -nE "s/^ARG[[:space:]]+${name}=\"?([^\"[:space:]]+)\"?[[:space:]]*$/\1/p"`;
   the pinning check run first guarantees each is declared exactly once before the first `FROM`. Tracks
   `stale=0` and `failed=0`; after the loop exits 1 if `failed`, 3 if `stale`, else 0. A lookup is run as
   `if ! manifest="$(docker buildx imagetools inspect --format '{{json .Manifest}}' "$image:$tag")"; then … failed=1; continue; fi`
   so `set -e` does not abort before the other bases. The `::warning::`/`::error::` lines go to standard
   error, the table to standard output.
2. **Workflow** `.github/workflows/base-image-digests.yml` (AC3). One job `check`, one checkout step and one
   `run` step, shaped like:

   ```bash
   set -uo pipefail
   rc=0
   report="$(.github/scripts/check-base-image-digests.sh)" || rc=$?
   case "$rc" in
     0) echo "base image digests are current"; exit 0 ;;
     3) ;;
     *) exit "$rc" ;;
   esac
   title="Base image digests are stale"
   body="$(printf '%s\n\n%s\n\n%s\n' "<fixed sentence>" "$report" "<fixed pointer to docs/CONTRIBUTING.md, Base image digests>")"
   number="$(gh api --paginate "repos/$GH_REPO/issues?state=open&per_page=100" \
     --jq '.[] | select(.pull_request == null and .title == "Base image digests are stale") | .number' | head -n1)"
   if [ -n "$number" ]; then
     gh api -X PATCH "repos/$GH_REPO/issues/$number" -f body="$body" > /dev/null
   else
     gh api "repos/$GH_REPO/issues" -f title="$title" -f body="$body" \
       -f 'labels[]=dependencies' -f 'labels[]=area: docker' > /dev/null
   fi
   ```

   The title is a literal in the `--jq` filter (no shell interpolation into jq).
3. **CI** (AC4), in `release-build` after `Check builder Go version`:

   ```yaml
   - name: Check base image digests
     run: |
       set -uo pipefail
       rc=0
       .github/scripts/check-base-image-digests.sh || rc=$?
       if [ "$rc" -eq 3 ]; then
         exit 0
       fi
       exit "$rc"
   ```

   with a one-line comment that a stale digest is only a warning here (record 0055).
4. **Dockerfile guard** (AC5), exact lines after `WORKDIR /src`:

   ```dockerfile
   # The builder's Go version must match BASE_BUILD_TAG, so a digest read for another Go version fails
   # the build (record 0055). Runs before go.mod is copied, so no toolchain switch can answer instead.
   ARG BASE_BUILD_TAG
   RUN want="go${BASE_BUILD_TAG%%-*}"; got="$(go env GOVERSION)"; \
       case "$got" in "$want"|"$want".*) ;; \
         *) echo "builder Go $got does not match BASE_BUILD_TAG $BASE_BUILD_TAG (want $want)" >&2; exit 1 ;; \
       esac
   ```

   Header comment lines 3–5 change to: digests are refreshed by hand in a pull request; a weekly workflow
   and the CI release build check report a stale digest (`docs/CONTRIBUTING.md`, records 0041 and 0055).

### Accepted forms (guards on input a parser or tool consumes)

**`ARG` defaults read by the digest script** (BuildKit is the real consumer). BuildKit accepts: `ARG name`,
`ARG name=value`, `ARG name="value"`, `ARG name='value'`, several names on one line, instructions in any
case and indented, continuation with `\` (or the `escape` character), comments, a UTF-8 byte order mark,
`# syntax=`/`# escape=` directives (also `//` form), and variable expansion inside a default
(`ARG X=${Y}`). Behavior:

| Form | Handled by | Result |
| ---- | ---------- | ------ |
| `ARG BASE_X_Y="v"` / `ARG BASE_X_Y=v`, upper-case, unindented, once before first `FROM` | read | accepted |
| lower-case or indented `arg`/`from`, multi-name `ARG`, `ARG` continued with `\` or backtick, BOM, `syntax`/`escape` directive in `#` or `//` form | pinning script (run first) | exit 1 |
| a `_IMAGE`/`_TAG`/`_DIGEST` declared twice, after the first `FROM`, or without a default | pinning script | exit 1 |
| `ARG BASE_X_TAG='v'` (single quotes) | read as `'v'`; tag/image pattern rejects `'` (a digest is already rejected by the pinning script) | exit 1 |
| `ARG BASE_X_TAG=${Y}` or any `$`, space, `:`, `@`, leading `-` in image or tag | image/tag pattern | exit 1 |
| trailing comment `ARG BASE_X_TAG=v # c` | read yields nothing; pinning script's "non-empty default" fails | exit 1 |
| `FROM --platform=… ${…}@${…}` or any other `FROM` shape | pinning script `FROM` pattern | exit 1 |
| bare `ARG BASE_BUILD_TAG` inside a stage (the new guard line) | not matched by either reader (both require `=`), not a multi-name or continued line | accepted, ignored |

**Registry answer** (`imagetools inspect` JSON): only `.digest` matching `^sha256:[0-9a-f]{64}$` is used;
a single-platform manifest instead of an index still has a `digest` and would be compared as is (a
change of the tag from index to single manifest then shows as stale, which is the right signal). Lookup
errors (401, 404, 429, network) and an empty or non-matching result are errors (exit 1).

**`BASE_BUILD_TAG` in the in-build guard** (dash in the `golang` image). The pinning and builder-version
scripts already restrict the default to `^[0-9]+\.[0-9]+([.-].*)?$`; an override by `--build-arg` is
possible locally (the release passes none). Checked at plan time with a stubbed `go`:

| `BASE_BUILD_TAG` | `GOVERSION` | Result |
| ---------------- | ----------- | ------ |
| `1.27-trixie` | `go1.27.3`, `go1.27` | pass |
| `1.27-trixie` | `go1.26.9`, `go1.270.1`, empty (go failed) | fail |
| `1.27.3-trixie` | `go1.27.3` | pass |
| `1.27.3-trixie` | `go1.27.4` | fail |
| `1.27` | `go1.27.1` | pass |
| `1.2` | `go1.27.1` | fail |
| `*`, `1.*`, `1.2?` (glob characters by override) | `go1.27.1` | fail (quoted pattern parts match literally) |

Known limit, stated in 0055: variants of the same version (`1.27-trixie` vs `1.27-bookworm`) are not
distinguished.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| CI | `.github/scripts/check-base-image-digests.sh` | new, mode 100755 |
| CI | `.github/workflows/base-image-digests.yml` | new |
| CI | `.github/workflows/ci.yml` | one step added to `release-build` |
| Deploy | `deploy/backend/Dockerfile` | guard in build stage, header comment |
| Docs | `docs/CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, `.squad/project.md` | see *Documentation updates* |
| Records | `docs/decisions/0055-…md` | new, Proposed (written with this plan) |

## Signatures (for the Dev's skeleton)

None (no Go code). Interfaces of the new script, for the Dev and the reviewers:

- `.github/scripts/check-base-image-digests.sh` — no arguments, run from the repository root; needs
  `docker` with `buildx` and `jq` (both on `ubuntu-latest`); stdout: Markdown table; stderr: `::warning::`
  and `::error::` lines; exit 0 current / 3 stale / 1 error.

## Test files

None: no production or test code changes (*Verification without tests*). No existing test calls a changed
signature.

## Documentation updates

Made by the Dev:

- `docs/CONTRIBUTING.md`:
  - *Versioning and releases* intro (line 84 list of records): add
    `[0055](decisions/0055-stale-base-image-digests-reported-weekly-builder-go-checked-in-build.md)`.
  - *Cutting a release* (line 90): before tagging, run the *Base image digests* workflow (Actions, *Run
    workflow*) or check that the latest `Release build check` on `main` has no stale-digest warning, and
    that no issue *Base image digests are stale* is open.
  - *Base image digests* (lines 132–141): keep the manual refresh text; add that the weekly workflow
    `.github/workflows/base-image-digests.yml` (Mondays, and on manual dispatch) compares every
    `BASE_<NAME>_DIGEST` with the current index digest of its tag and opens or updates the issue *Base image
    digests are stale*; the `Release build check` shows the same as a warning; the refresh pull request
    should close that issue (`Closes #n`); the script can be run locally from the repository root
    (needs `docker buildx` and `jq`); the build stage fails when the builder's Go version does not match
    `BASE_BUILD_TAG`; if GitHub disables the scheduled workflow after 60 days without activity, re-enable it.
  - *Release build check on pull requests* (lines 145–148): mention the digest check and that a stale
    digest is a warning there, not a failure.
- `docs/ARCHITECTURE.md`, *Deployment*, bullet at lines 242–245: replace "and the digests are refreshed by
  hand" with: the digests are refreshed by hand in a pull request, a weekly workflow reports a stale digest
  as an issue, and the build stage checks that the builder's Go version matches its tag; add 0055 to the
  *Records* line (line 255–258).
- `.squad/project.md`, security area 13: add `.github/workflows/base-image-digests.yml` and
  `.github/scripts/` to the file list, and one sentence to the goal: the scheduled digest check holds only
  `contents: read` and `issues: write`, writes only regex-checked image, tag and digest values into the
  issue, and never writes to the repository; add 0055 to its records. (This file describes the product and
  becomes untrue without it, `.squad/routing.md`, *Scope of a product PR*.)
- `README.md`: none (no configuration, install or user-visible change).

## Architecture check

- Digest pinning of every base image (ARCHITECTURE *Deployment*, security area 13) is unchanged: no `FROM`
  changes, the release still passes no `BASE_*` argument, and both existing checks run unchanged.
- "No `${{ }}` inside a `run:` script; every value through `env:`; checkout does not persist the job token;
  every action pinned by SHA" — the new workflow follows each rule (AC3 c, d).
- Release trigger rules (0053): `release.yml` is untouched; the new workflow publishes nothing and is not a
  release trigger.
- No guarantee from `.squad/project.md` *Guarantees* is touched.

## Security considerations

- New write permission: `issues: write` on the `GITHUB_TOKEN` of a scheduled/dispatched job on the default
  branch. It can create and edit issues only; it receives no secret, no environment, no
  `contents: write`. Anyone who can dispatch it already has write access.
- Untrusted input: the registry's JSON answer. Only a value matching `^sha256:[0-9a-f]{64}$` is used and
  written to the issue; anything else is an error. Image and tag come from the repository's Dockerfile
  and are pattern-checked before reaching `docker` (no option injection through a leading `-`) and before
  reaching the issue body (no Markdown or mention injection: no `@`, spaces, brackets or backticks).
- The issue title is a literal; the jq filter contains no shell-interpolated value.
- The in-build guard uses only `BASE_BUILD_TAG`, quoted in the `case` pattern; an override cannot make it
  pass for another version (table above). It runs before any repository file is copied in.
- Availability: a registry outage fails the CI release build check one step earlier than the build it
  already depended on; the release workflow gains no new dependency.

## Decision records

- `docs/decisions/0055-stale-base-image-digests-reported-weekly-builder-go-checked-in-build.md` (Proposed)
  — extends 0041 (0041 stays Accepted, like 0053 amends 0037); options (a) PR / fail-only / issue, (b), (c),
  freshness gate, in-build guard variants.

## Out of scope / follow-ups

- Automatic refresh pull requests (rejected in 0055, option 1); revisit if Dependabot learns ARG-based
  `FROM` lines (0041).
- Closing the stale issue automatically when digests become current: left to the refresh pull request's
  `Closes #n`.
- Post-merge: the maintainer runs *Base image digests* once by `workflow_dispatch` (AC3 e) — for the PR's
  *Next steps*.
