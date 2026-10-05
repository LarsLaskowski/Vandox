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
    any of them is passed to a command; each check is a **whole-string match** per AC2 i;
  - d. resolves `<image>:<tag>` with `docker buildx imagetools inspect --format '{{json .Manifest}}'` and
    `jq -r .digest`; a non-zero exit of the lookup (e.g. HTTP 429), a non-zero exit of `jq` (e.g. the
    answer is an array) or a result that is not, as a whole string (AC2 i), `^sha256:[0-9a-f]{64}$` (e.g.
    `null`, empty, several lines, an embedded newline) is a **lookup error**:
    `::warning::lookup of <image>:<tag> failed` on standard error and exit status 4, never a stale or
    current result; the rejected value is written nowhere (neither to the table nor to a warning);
  - e. prints to standard output a Markdown table `| Base | Image | Pinned digest | Current digest | State |`
    with one row per base (`BASE_<NAME>`, `<image>:<tag>`, the two digests, the state per AC2 h);
  - f. for each stale base prints `::warning::BASE_<NAME>_DIGEST is stale: <image>:<tag> is now <current>, pinned <pinned>`
    to standard error;
  - g. exit status: 1 on any error other than a lookup error (pinning check, patterns of AC2 c, a missing
    tool); otherwise 4 on any lookup error; otherwise 3 when at least one digest is stale; otherwise 0.
    Precedence 1 > 4 > 3 > 0; every base is still looked up so the output is complete;
  - h. state column: `current`; or for a stale base `stale (Go <ver> available)` when
    `docker buildx imagetools inspect --format '{{json .Image}}' <image>:<tag>` succeeds and
    `."linux/amd64".config.Env` holds `GOLANG_VERSION=<ver>` with `<ver>` matching
    `^[0-9]+\.[0-9]+(\.[0-9]+)?$` as a whole string (AC2 i; two `GOLANG_VERSION` entries give two lines and
    do not match); otherwise plain `stale`. This second lookup runs only for a stale base, and its failure,
    a `jq` failure or a missing/non-matching value never changes the exit status (informational only);
  - i. every pattern check on a value (image, tag, pinned digest, resolved digest, Go version) is a bash
    whole-string match `[[ $v =~ $re ]]` with the anchored pattern held in a variable (`re='^…$'`), never
    `printf '%s' "$v" | grep -Eq` (grep matches line by line, so a value with an embedded newline whose
    first or any line matches would pass). The script sets `export LC_ALL=C` right after `set -euo pipefail`,
    so the bracket ranges `[a-z]`, `[0-9a-f]` and `[A-Za-z]` are byte ranges in every locale. Only
    regex-matched values reach standard output, the `::warning::` lines or a command line.
- [ ] AC3: `.github/workflows/base-image-digests.yml` exists with:
  - a. triggers exactly `schedule` (`cron: "0 5 * * 1"`) and `workflow_dispatch`; no `push`,
    `pull_request` or other trigger;
  - b. top-level `permissions: {}`; one job with exactly `contents: read` and `issues: write`;
    `concurrency` group `base-image-digests`, `cancel-in-progress: false`; no job-level or workflow-level
    `env:`;
  - c. `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1` with
    `persist-credentials: false`; no other action;
  - d. no `${{ }}` expression inside any `run:` script; `GH_TOKEN: ${{ github.token }}` and
    `GH_REPO: ${{ github.repository }}` only through the step-level `env:` of the issue step (AC3 e), so
    the step that runs the script, `docker buildx` and `jq` has no token in its environment;
  - e. two `run` steps, each with `set -euo pipefail`. Step `Check base image digests` (id `digests`, no
    `env:`) runs the script. Status 0: logs "current" and succeeds; status 3: writes the issue body (AC3 f)
    to `$RUNNER_TEMP/base-image-digests.md` and the fixed line `stale=true` to `$GITHUB_OUTPUT`; any other
    status (1, 4, …): fails the job with `::error::` (so no write follows). Step `Report stale digests`
    runs only `if: steps.digests.outputs.stale == 'true'`, reads the body from that file, then
    looks up open issues through `gh api` (REST, `--paginate`) and keeps only those
    with `.pull_request == null`, `.user.login == "github-actions[bot]"` and the exact title
    `Base image digests are stale` (an issue with that title opened by anyone else is ignored and never
    edited); a failed lookup (non-zero exit of `gh api`) fails the job with `::error::` before any write,
    never falls through to creating an issue; the first match is taken without a pipe into `head`; if one
    exists, `PATCH`es its body; otherwise creates it with the labels `dependencies` and `area: docker`
    (both exist in the repository);
  - f. the issue body is the script's table plus fixed text pointing to `docs/CONTRIBUTING.md`,
    *Base image digests*; no other variable content;
  - g. writes no file into the repository (the body file lives under `$RUNNER_TEMP`, outside the
    checkout), pushes nothing, opens no pull request, closes no issue.
- [ ] AC4: In `.github/workflows/ci.yml`, job `release-build`, a step `Check base image digests` follows
  `Check builder Go version`: it runs the script; status 0 and 3 pass (3 leaves the warning annotations);
  status 4 (registry lookup error) passes with `::warning::base image digest lookup failed; freshness not
  checked (record 0055)`; any other status (1: pinning or pattern error) fails the job. No other change to
  `ci.yml`.
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
| AC2 a–h | Dev runs the script in the repository root and in a scratch `git worktree` with (1) the committed Dockerfile, (2) one `_DIGEST` default changed to another valid sha256 (expect 3, warning, table row `stale`; for `BASE_BUILD`, if Docker Hub answers, `stale (Go <ver> available)`; for `BASE_RUNTIME` plain `stale`), (3) a tag default set to `-x` and to `'nonroot'` (expect 1 before any lookup), (4) a FROM line broken (expect 1 from the pinning check), (5) an unresolvable tag such as `nonroot-doesnotexist` (expect 4), (6) case (5) together with case (3) on the other base (expect 1, precedence); with a stub `docker` first on `PATH` (a script in a scratch directory that prints a fixed JSON per `--format`): (7) `.Manifest` answer `{"digest":"sha256:<64 hex>\n@LarsLaskowski [x](https://e.vil)"}` (JSON `\n`, so `jq -r` yields an embedded newline) — expect 4, and neither standard output nor standard error contains `@LarsLaskowski` or `e.vil`; (8) `.Manifest` answer a JSON array `[{"digest":"sha256:<64 hex>"}]` — expect 4; (9) `.Manifest` answer a valid digest different from the pinned one and `.Image` answer `{"linux/amd64":{"config":{"Env":["GOLANG_VERSION=1.27.3","GOLANG_VERSION=1.27.4"]}}}` — expect 3 and the state column plain `stale`; (10) as (9) with a single `GOLANG_VERSION=1.27.3` — expect 3 and `stale (Go 1.27.3 available)`; (11) every `grep` hit in the script reads the Dockerfile (e.g. the `FROM` lines), none checks a value variable, and `export LC_ALL=C` follows `set -euo pipefail`; reports each exit status and output. A Docker Hub 429 during a run is itself the lookup-error check for `golang` (expect 4); the `gcr.io` runtime base resolves from this session. `bash -n` on the script; `shellcheck` if available. Then the PR's *Release build check* run on a GitHub runner (AC4) shows the real result for both bases | Dev (step 6), Reviewer and Security re-run (1)–(3) and (7)–(9) read-only in a scratch worktree (step 8) |
| AC3 a–d, f, g | Read-only review of the workflow file; `grep -n '\${{' .github/workflows/base-image-digests.yml` shows hits only in the `env:` lines of the step `Report stale digests`, and `grep -n 'GH_TOKEN'` only there | Reviewer, Security (step 8) |
| AC3 e | Cannot run before merge (a `workflow_dispatch` needs the file on `main`). The status handling is a `case` on the script status, the `if:` on the step output and the lookup's `if !` guard, reviewed in step 8; the `--jq` filter is run read-only once by the Dev with `gh api --paginate "repos/LarsLaskowski/Vandox/issues?state=open&per_page=100" --jq '<filter>'` (expect no output and exit 0) and with the repository name misspelled (expect non-zero exit); after the merge the maintainer runs the workflow once by `workflow_dispatch` and confirms a green run with no issue (digests current) — recorded as a post-merge check in the PR's *Next steps* | Reviewer, Security (step 8); maintainer after merge |
| AC4 | The PR's CI run: `Release build check` contains the new step, green, with a warning only if a digest is stale or a lookup failed; the `case` mapping (0/3/4 pass, other fail) is reviewed in the diff | Orchestrator reads the run (step 11), Reviewer (diff) |
| AC5 | The PR's `Release build check` builds the image (guard passes with the pinned builder). The guard command is run under `sh` with a stub `go` for the cases in *Accepted forms* (as done at plan time); a local `docker build --build-arg BASE_BUILD_TAG=1.26-trixie` (expect failure at the guard) if Docker Hub allows the pull. Both existing scripts run against the new Dockerfile and pass | Dev (step 6), Security (step 8) |
| AC6, AC7 | `git diff --stat origin/main...HEAD` and the `grep` in AC7 | Reviewer (step 8), Lead (step 9) |

## Approach

1. **Script** `.github/scripts/check-base-image-digests.sh` (AC2). Header comment style and
   `f=deploy/backend/Dockerfile` as in the two existing scripts, but **not** their value checks: the
   existing scripts check values with `printf '%s' "$v" | grep -Eq '^…$'`, which matches line by line.
   There that is safe (each value comes from one `sed`-matched Dockerfile line, declared once), but the
   registry answer and `jq -r` output can hold several lines. The new script starts with
   `set -euo pipefail` and `export LC_ALL=C`, defines its patterns once as variables
   (`image_re='^[a-z0-9][a-z0-9._/-]*$'`, `tag_re='^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$'`,
   `digest_re='^sha256:[0-9a-f]{64}$'`, `go_re='^[0-9]+\.[0-9]+(\.[0-9]+)?$'`) and checks every value with
   `[[ $v =~ $digest_re ]]` and so on (bash ERE without `REG_NEWLINE`: `^`/`$` anchor the whole string,
   verified at plan-revise time — a digest with an embedded newline is rejected by `[[ =~ ]]` and passed by
   `grep -Eq`). Reads defaults with the same pattern
   the existing scripts use, `sed -nE "s/^ARG[[:space:]]+${name}=\"?([^\"[:space:]]+)\"?[[:space:]]*$/\1/p"`;
   the pinning check run first guarantees each is declared exactly once before the first `FROM`. Tracks
   `stale=0`, `lookup_failed=0` and `failed=0`; after the loop exits 1 if `failed`, 4 if `lookup_failed`,
   3 if `stale`, else 0. A lookup is run as
   `if ! manifest="$(docker buildx imagetools inspect --format '{{json .Manifest}}' "$image:$tag")"; then … lookup_failed=1; continue; fi`
   so `set -e` does not abort before the other bases; the digest is then taken as
   `if ! current="$(jq -r .digest <<<"$manifest")" || ! [[ $current =~ $digest_re ]]; then … lookup_failed=1; continue; fi`
   (a `jq` error, `null`, empty or multi-line value is a lookup error; the value is not printed). For a stale base only, the Go version is read with
   `if config="$(docker buildx imagetools inspect --format '{{json .Image}}' "$image:$tag")"` and
   `jq -r '(."linux/amd64".config.Env // .config.Env // [])[] | select(startswith("GOLANG_VERSION=")) | ltrimstr("GOLANG_VERSION=")'`
   (the `// .config.Env` covers a single-platform manifest; `jq` failure inside the `if` is tolerated),
   then checked with `[[ $go =~ $go_re ]]` (two entries give two lines and fail the match); checked at plan time that for an index `.Image` is a map keyed by platform
   (`gcr.io/distroless/static-debian13:nonroot`: keys `linux/amd64` … `linux/s390x`, `Env` without
   `GOLANG_VERSION`); the `golang` lookup was rate-limited (429), so the presence of `GOLANG_VERSION` in its
   `Env` (the official image's documented convention) is confirmed by the Dev's run (5)/(2) or on the PR's
   CI run. The `::warning::`/`::error::` lines go to standard error, the table to standard output.
2. **Workflow** `.github/workflows/base-image-digests.yml` (AC3). One job `check` with no job-level `env:`,
   a checkout step and two `run` steps. The script (and with it `docker buildx` and `jq`, which talk to the
   registries) runs in a step without the job token in its environment; only the issue step gets
   `GH_TOKEN`/`GH_REPO`. The body crosses the step boundary as a file under `$RUNNER_TEMP` (outside the
   checkout, removed with the runner), the "stale" signal as a fixed step output.

   ```yaml
   - name: Check base image digests
     id: digests
     run: |
       set -euo pipefail
       rc=0
       report="$(.github/scripts/check-base-image-digests.sh)" || rc=$?
       case "$rc" in
         0) echo "base image digests are current"; exit 0 ;;
         3) ;;
         *) echo "::error::base image digest check failed with status $rc" >&2; exit 1 ;;
       esac
       printf '%s\n\n%s\n\n%s\n' "<fixed sentence>" "$report" "<fixed pointer to docs/CONTRIBUTING.md, Base image digests>" \
         > "$RUNNER_TEMP/base-image-digests.md"
       echo "stale=true" >> "$GITHUB_OUTPUT"
   - name: Report stale digests
     if: steps.digests.outputs.stale == 'true'
     env:
       GH_TOKEN: ${{ github.token }}
       GH_REPO: ${{ github.repository }}
     run: |
       …
   ```

   The `Report stale digests` script:

   ```bash
   set -euo pipefail
   title="Base image digests are stale"
   body="$(< "$RUNNER_TEMP/base-image-digests.md")"
   if ! numbers="$(gh api --paginate "repos/$GH_REPO/issues?state=open&per_page=100" \
       --jq '.[] | select(.pull_request == null and .user.login == "github-actions[bot]" and .title == "Base image digests are stale") | .number')"; then
     echo "::error::looking up the open issue failed; nothing written" >&2
     exit 1
   fi
   number="${numbers%%$'\n'*}"
   if [ -n "$number" ]; then
     gh api -X PATCH "repos/$GH_REPO/issues/$number" -f body="$body" > /dev/null
   else
     gh api "repos/$GH_REPO/issues" -f title="$title" -f body="$body" \
       -f 'labels[]=dependencies' -f 'labels[]=area: docker' > /dev/null
   fi
   ```

   The title and the bot login are literals in the `--jq` filter (no shell interpolation into jq). No
   `creator=` query parameter is used: whether it accepts a bot login is not verified, and an empty
   answer there would create a duplicate issue every week; the `.user.login` filter is authoritative.
   `${numbers%%$'\n'*}` takes the first line without a pipe (no SIGPIPE under `pipefail`); `number` is
   only ever a number from the API's `.number` field.
3. **CI** (AC4), in `release-build` after `Check builder Go version`:

   ```yaml
   - name: Check base image digests
     run: |
       set -uo pipefail
       rc=0
       .github/scripts/check-base-image-digests.sh || rc=$?
       case "$rc" in
         0|3) exit 0 ;;
         4) echo "::warning::base image digest lookup failed; freshness not checked (record 0055)"; exit 0 ;;
         *) exit "$rc" ;;
       esac
   ```

   with a one-line comment that a stale digest or a failed registry lookup is only a warning here; the
   pinning and pattern errors (status 1) still fail (record 0055).
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
| any read that yields several lines (only possible with a second declaration) | pinning script; independently the whole-string image/tag/digest match | exit 1 |
| bare `ARG BASE_BUILD_TAG` inside a stage (the new guard line) | not matched by either reader (both require `=`), not a multi-name or continued line | accepted, ignored |

**Registry answer** (`imagetools inspect` JSON, read by `jq -r`): only a `.digest` that is, as a whole
string, `^sha256:[0-9a-f]{64}$` (bash `[[ =~ ]]`, `LC_ALL=C`) is used; a single-platform manifest instead
of an index still has a `digest` and would be compared as is (a change of the tag from index to single
manifest then shows as stale, which is the right signal). Behavior per answer form:

| Answer | Result |
| ------ | ------ |
| lookup exits non-zero (401, 404, 429, network) | lookup error, exit 4 |
| `.Manifest` an object with a valid `digest` | used |
| `digest` missing or `null` (`jq -r` prints `null`), empty, upper-case hex, other algorithm | lookup error, exit 4 |
| `digest` with an embedded newline (JSON `\n`) or trailing text, e.g. Markdown, a mention, a link | lookup error, exit 4; value printed nowhere |
| `.Manifest` an array or a scalar (`jq` exits non-zero) | lookup error, exit 4 |
| several JSON documents (`jq -r` prints several lines) | lookup error, exit 4 |

From the image config only a `GOLANG_VERSION` value that is, as a whole string,
`^[0-9]+\.[0-9]+(\.[0-9]+)?$` is used (written into the state column); anything else (absent, `rc`/`beta`
versions, other characters, several `GOLANG_VERSION` entries — `jq` prints several lines, which the
whole-string match rejects —, a `jq` error, a failed lookup) yields plain `stale`.

**GitHub issue list** (`gh api` JSON): only issues with `.pull_request == null`,
`.user.login == "github-actions[bot]"` and the exact literal title are candidates; an issue with the same
title from any other author (or a pull request) is never edited. A failed list call stops the job before
any write.

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
  and `::error::` lines; exit 0 current / 3 stale / 4 registry lookup error / 1 any other error
  (precedence 1 > 4 > 3 > 0).

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
    digest or a failed registry lookup is a warning there, not a failure.
  - In *Base image digests*, also: the issue's state column shows the Go version the `golang` tag now
    carries (`stale (Go <ver> available)`); a `golang` or distroless digest moves often without a Go
    change (Debian package updates), so a stale report is routine; refresh at least when a new Go patch is
    shown or before a release.
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
- Untrusted input: the registry's JSON answer. Only a value that matches `^sha256:[0-9a-f]{64}$` as a
  whole string (bash `[[ =~ ]]` under `LC_ALL=C`, not line-wise `grep`) is used and written to the issue;
  anything else is a lookup error and is printed nowhere, so a multi-line answer cannot smuggle
  mentions, links or Markdown into the bot-authored issue or the `::warning::` annotations.
- Token exposure: `GH_TOKEN` is set only on the issue step. The step that runs `docker buildx`, `jq` and
  the registry lookups has no GitHub token in its environment, and checkout persists none. Image and tag come from the repository's Dockerfile
  and are pattern-checked before reaching `docker` (no option injection through a leading `-`) and before
  reaching the issue body (no Markdown or mention injection: no `@`, spaces, brackets or backticks).
- The issue title is a literal; the jq filter contains no shell-interpolated value.
- Issue spoofing: the repository is public, so anyone can open an issue titled *Base image digests are
  stale*. The lookup only matches issues opened by `github-actions[bot]`, so the workflow never edits an
  outsider's issue (where the outsider would stay author and could blank the body or plant a decoy); a
  decoy issue only means the bot opens its own next to it.
- Lookup failure: the issue list call runs under `set -euo pipefail` inside an `if !` guard; a failure
  ends the job before any write, so a GitHub API error cannot create a duplicate issue.
- The Go version shown in the state column comes from the registry's image config and is written into
  the issue only after matching `^[0-9]+\.[0-9]+(\.[0-9]+)?$` as a whole string.
- The in-build guard uses only `BASE_BUILD_TAG`, quoted in the `case` pattern; an override cannot make it
  pass for another version (table above). It runs before any repository file is copied in.
- Availability: a registry lookup error is only a warning in the CI release build check (status 4), so a
  rate limit on the freshness lookup does not fail unrelated pull requests; the image build in the next
  step still pulls both bases as before. The release workflow gains no new dependency.

## Decision records

- `docs/decisions/0055-stale-base-image-digests-reported-weekly-builder-go-checked-in-build.md` (Proposed)
  — extends 0041 (0041 stays Accepted, like 0053 amends 0037); options (a) PR / fail-only / issue, (b), (c),
  freshness gate, in-build guard variants, report content, issue lookup, token placement (option 10),
  value check method (option 11).

## Challenge

Devil's Advocate: `OBJECTIONS`, 0 major, 3 minor. All three accepted.

1. *Issue lookup unchecked and matched on title only* — **accepted.** The workflow script now runs under
   `set -euo pipefail`, wraps the `gh api` list call in `if ! numbers="$(…)"` and fails with `::error::`
   before any write, and takes the first line by parameter expansion instead of `| head -n1`. The `--jq`
   filter additionally requires `.user.login == "github-actions[bot]"`, so an outsider's issue with the
   same title is never edited. A `creator=` query parameter was not added (unverified for bot logins; an
   empty answer would create duplicates). Changed: AC3 e, Approach step 2, *Accepted forms* (GitHub issue
   list), *Security considerations*, *Verification* AC3 e, record 0055.
2. *Registry error fails unrelated pull requests in CI* — **accepted.** The script now separates a
   registry lookup error (exit 4) from a configuration error (exit 1; precedence 1 > 4 > 3 > 0). In
   `ci.yml` status 4 becomes a `::warning::` and passes; status 1 (pinning or pattern error, which the
   docker build would not catch for the tag) still fails. The scheduled workflow fails on 4 as before.
   Dropping the CI step was not chosen: it is the only check that shows a stale digest on the pull request
   that should refresh it. Changed: AC2 d/g, AC4, Approach steps 1 and 3, *Signatures*, *Security
   considerations*, *Documentation updates*, record 0055.
3. *"stale" is near-permanent noise and hides the Go patch lag* — **accepted.** For a stale base the
   script reads `GOLANG_VERSION` from the current tag's `linux/amd64` image config and shows
   `stale (Go <ver> available)`; informational only (never changes the exit status), pattern-checked
   before it reaches the issue. The `.Image` format (map keyed by platform for an index) was checked on
   distroless at revise time; the `golang` lookup was rate-limited, so the `GOLANG_VERSION` presence is
   confirmed in the Dev's run or the PR's CI. The record now also states the expected noise level.
   Changed: AC2 h, Approach step 1, *Accepted forms*, *Documentation updates*, record 0055.

Security plan review (1st): `CHANGES_REQUIRED`, 1 blocking, 1 non-blocking. Both accepted. The whole
*Accepted forms* section was re-checked against whole-string matching: the `ARG` table gains a row for a
multi-line read; the registry answer is now a table of answer forms; the GitHub issue list and the
`BASE_BUILD_TAG` guard are unaffected (`number` comes only from `.number`, the guard does not use grep).

- B1 *Line-wise `grep -Eq` lets a multi-line registry value through* — **accepted.** Reproduced at revise
  time: for a `jq -r` value `sha256:<64 hex>\n@x [x](https://e.vil)`, `printf '%s' "$v" | grep -Eq
  '^sha256:[0-9a-f]{64}$'` matches and `[[ $v =~ $re ]]` rejects; a `.Manifest` array makes `jq` exit 5;
  two `GOLANG_VERSION` entries fail the whole-string Go pattern. Every value check is now a bash
  whole-string match with `export LC_ALL=C` (new AC2 i, referenced from AC2 c, d, h); a `jq` failure is a
  lookup error; a rejected value is printed nowhere. Approach step 1 no longer says the structure follows
  the existing scripts without qualification and states why their `grep` checks are safe there and not
  here (existing scripts unchanged, AC6). Verification cases (7)–(11) added: embedded newline → 4 with no
  injected text in the output, array → 4, doubled `GOLANG_VERSION` → 3 with plain `stale`. Changed: AC2 c,
  d, h, i, *Verification* AC2, Approach step 1, *Accepted forms*, *Security considerations*, record 0055.
- N1 *Token in the environment of `docker buildx`* — **accepted.** The workflow now has two `run` steps:
  `Check base image digests` (no `env:`) runs the script and, on status 3, writes the body to
  `$RUNNER_TEMP/base-image-digests.md` and `stale=true` to `$GITHUB_OUTPUT`; `Report stale digests` runs
  only on that output and alone holds `GH_TOKEN`/`GH_REPO`. No job- or workflow-level `env:`. Changed:
  AC3 b, d, e, g, *Verification* AC3, Approach step 2, *Security considerations*, record 0055 (option 10).

## Out of scope / follow-ups

- Automatic refresh pull requests (rejected in 0055, option 1); revisit if Dependabot learns ARG-based
  `FROM` lines (0041).
- Closing the stale issue automatically when digests become current: left to the refresh pull request's
  `Closes #n`.
- Post-merge: the maintainer runs *Base image digests* once by `workflow_dispatch` (AC3 e) — for the PR's
  *Next steps*.
