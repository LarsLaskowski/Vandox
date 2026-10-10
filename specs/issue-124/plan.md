# Plan: Report a stale syft image pin in the weekly digest check

Source: Issue #124
Status: Draft
Tier: security — the change adds a script under `.github/scripts/` and a job with `issues: write` to
`.github/workflows/base-image-digests.yml`, both listed in security area 13 (*Release pipeline and published
artifacts*) of `.squad/project.md`, and it edits that security area's text.

## Problem / root cause

Not a bug: record 0037 pins the SBOM generator as a constant in `.github/scripts/generate-sbom.sh:13`
(`syft_image='ghcr.io/anchore/syft:v1.54.0@sha256:0356562f…ca7c'`). Nothing reads that constant except the script
itself, so nobody notices when it ages. 0037 names the follow-up under *Consequences* ("report it in the weekly
base-image digest workflow").

Claims of the issue, checked against the repository on 2026-10-10:

- "Record 0056 (#119) pins the SBOM generator in `generate-sbom.sh` as `ghcr.io/anchore/syft:vX.Y.Z@sha256:…`":
  the form is **confirmed** (`generate-sbom.sh:13`, checked with `syft_re` at `generate-sbom.sh:71`). The record
  number is **refuted**: 0056 was merged into 0037 by PR #145 ("Merge the five release records into 0037"), and
  there is no file 0056.
- "Dependabot cannot read that reference": **confirmed**. `.github/dependabot.yml` has the ecosystems
  `github-actions`, `gomod`, `nuget`, `dotnet-sdk` and `docker` (`/deploy/backend`), and none of them reads a shell
  script.
- "refreshed by hand (`docs/CONTRIBUTING.md`, *SBOM generator*)": **confirmed** (`docs/CONTRIBUTING.md`, section
  *SBOM generator*).
- "`.github/workflows/base-image-digests.yml` (record 0055)": the workflow is **confirmed**; the record number is
  **refuted**: 0055 was merged into 0041 (the workflow's header comment already says 0041).
- "Same restrictions as the existing digest check: no token in the lookup step, only regex-checked values
  written": **confirmed** as the existing behavior. The step *Check base image digests* has no `env:` and no token;
  `check-base-image-digests.sh` checks image, tag and digest as whole strings with `[[ =~ ]]` before they reach a
  command or the table; `.squad/project.md`, area 13, states it as a security goal.
- Checked against the artifacts (anonymous reads, 2026-10-10): the ghcr.io index digest of
  `ghcr.io/anchore/syft:v1.54.0` is `sha256:0356562f495d432056237fbea5cbc2d4839c9c75cd500784a66de2e7cc95ca7c`
  (equal to the pin), and syft has a newer release: `git ls-remote --tags --refs https://github.com/anchore/syft.git`
  lists `v1.54.1` as the highest `vX.Y.Z` tag, index digest
  `sha256:3eb5379ba7b409c3f4069b686110527af0c47df993fa5c10d13e7cf34f49b1aa`. The first run of the new check will
  therefore open the issue (expected, see *Out of scope*). `docker buildx imagetools inspect` and anonymous
  `git ls-remote` both work in this cloud session.

Related defect found on the way (not fixed here, see *Out of scope*): `.github/dependabot.yml` still has a
`package-ecosystem: docker` entry for `/deploy/backend` (re-added by PR #135, commit `f3ffa9c`), while record 0041
says "the Dependabot `docker` entry is removed because it finds nothing" and "Dependabot no longer covers `docker`".

## Acceptance criteria

New script `.github/scripts/check-sbom-generator-pin.sh` (*the script*):

- [ ] AC1 — **Pin read fail-closed, before any network access.** The script reads the pin from
  `.github/scripts/generate-sbom.sh` and continues only when all of these hold; otherwise it prints one
  `::error::` line to standard error and exits 1 without calling `git` or `docker`:
  1. exactly one line of the file starts with `syft_image=`;
  2. that line is, as a whole, `syft_image='<value>'` (regex `^syft_image='([^']*)'$`: single quotes, nothing
     before or after, no trailing space);
  3. the line before it does not end in a backslash;
  4. every other line that contains the text `syft_image` contains it only inside the expansions `$syft_image`
     or `${syft_image}` (after deleting those, no `syft_image` remains);
  5. `<value>` matches `^ghcr\.io/anchore/syft:v[0-9]+\.[0-9]+\.[0-9]+@sha256:[0-9a-f]{64}$` (the `syft_re` of
     `generate-sbom.sh`, verbatim), and its tag also matches the strict
     `^v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$` (no leading zero, at most 9 digits per
     part, so that bash arithmetic neither reads octal nor overflows).
  The forms bash accepts and the guard's answer to each are in *Security considerations*.
- [ ] AC2 — **Newest release without credentials.** The newest release is the highest tag of
  `https://github.com/anchore/syft.git` whose ref matches, as a whole string,
  `^refs/tags/(v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8}))$`, read with
  `git ls-remote --tags --refs` and compared numerically by major, minor, patch. Every other ref (pre-release
  such as `v1.0.0-rc.1` or `v0.1.0-beta.1`, a leading zero, a peeled `^{}` ref, a trailing CR, any other name) is
  ignored. `git` runs with `GIT_TERMINAL_PROMPT=0`, `GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL=/dev/null`,
  `-c credential.helper=` and outside any repository (a fresh `mktemp -d` directory as working directory with
  `GIT_CEILING_DIRECTORIES` set to its parent, removed by an `EXIT` trap), so neither a credential helper nor a
  checkout's `http.extraheader` can add a credential. A failing `git` or no matching tag is a lookup failure.
- [ ] AC3 — **Moved digest.** The current index digest of the pinned tag is read with
  `docker buildx imagetools inspect --format '{{json .Manifest}}' ghcr.io/anchore/syft:<pinned tag>` and
  `jq -r .digest`, and must match `^sha256:[0-9a-f]{64}$` as a whole string (otherwise a lookup failure). If it
  differs from the pinned digest, the pinned row's state is `stale` and the script prints
  `::warning::the syft pin's digest is stale: ghcr.io/anchore/syft:<tag> is now <current>, pinned <pinned>`.
- [ ] AC4 — **Newer release.** When the newest release is greater than the pinned tag, the script reads that tag's
  index digest the same way (a failure is a lookup failure), adds the newest-release row with state `newer` and
  prints `::warning::a newer syft release exists: <newest>, pinned <pinned tag>`. A newest release equal to or
  lower than the pinned tag adds no row.
- [ ] AC5 — **Output and exit status.** Standard output carries only this table (no other line; the output of
  `git`, `docker` and `jq` is captured in variables, their standard error goes to the job log):
  ```
  | Image | Tag | Role | Pinned digest | Current digest | State |
  | ----- | --- | ---- | ------------- | -------------- | ----- |
  | ghcr.io/anchore/syft | <pinned tag> | pinned | <pinned digest> | <current digest> | current or stale |
  | ghcr.io/anchore/syft | <newest tag> | newest release | - | <its digest> | newer |
  ```
  (the second row only under AC4; the pinned row is left out when its own lookup failed). Every cell is a
  constant or a value that passed one of the regular expressions above. Exit status: 0 the pin is the newest
  release and its digest is current; 3 a newer release or a moved digest; 4 a lookup failed; 1 any other error,
  including a missing `git`, `docker` or `jq` and any argument (the script takes none); precedence 1 > 4 > 3 > 0,
  as in `check-base-image-digests.sh`.
- [ ] AC6 — **Weekly report in a sibling issue.** `base-image-digests.yml` gets a second job `sbom-generator`
  (name *Check SBOM generator pin*, `runs-on: ubuntu-latest`, `permissions: contents: read, issues: write`), with
  the same checkout step (same commit SHA, `persist-credentials: false`), a step *Check SBOM generator pin*
  (`id: pin`, no `env:`, no token) that runs the script and, on exit 3 only, writes
  `$RUNNER_TEMP/sbom-generator-pin.md` from fixed text and the script's standard output and sets `stale=true`;
  on 0 it logs that the pin is current; on any other status it prints
  `::error::SBOM generator pin check failed with status <rc>` and fails the job without writing anything. A step
  *Report stale SBOM generator pin* (`if: steps.pin.outputs.stale == 'true'`, `GH_TOKEN`/`GH_REPO` in `env:`)
  updates the body of the open issue titled exactly `SBOM generator pin is stale` authored by
  `github-actions[bot]`, or creates it with the labels `dependencies` and `release`; a failed lookup of open issues
  writes nothing and fails. The body is:
  `The syft image pinned in .github/scripts/generate-sbom.sh is not the newest syft release, or the index digest of its tag has moved.`,
  a blank line, the table, a blank line,
  `Refresh the pin as described in docs/CONTRIBUTING.md, section SBOM generator.`
- [ ] AC7 — **Existing check unchanged, jobs independent.** The job `check` keeps its steps, permissions, issue
  title and labels byte for byte apart from the workflow's header comment, which names both reports and records
  0041 and 0037. The two jobs have no `needs:`, so a failure of one does not skip the other. Workflow-level
  `permissions: {}`, the triggers and the concurrency group stay as they are. No `${{ }}` appears inside any `run:`
  script.
- [ ] AC8 — **Scope.** `generate-sbom.sh`, `check-base-image-digests.sh`, `ci.yml` and `release.yml` are not
  changed; the pin itself is not refreshed in this change.
- [ ] AC9 — **Documentation** matches the built behavior: `docs/CONTRIBUTING.md` (*SBOM generator*, *Base image
  digests*, *Cutting a release*), `.squad/project.md` (area 13), record 0037 and `docs/ARCHITECTURE.md`
  (*Deployment*), as listed under *Documentation updates*.

## Verification without tests

The change touches no production or test code (a shell script under `.github/scripts/`, a workflow and
documentation). Steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* of step 6 are **not applicable**.
Each criterion is verified instead as follows.

Scenario runs (S1–S6) are made by the **Dev** in step 6 in a scratch `git worktree` (never in the working tree),
from the worktree's root, with the exit status and the complete standard output and standard error pasted into
the Dev's report. The **Reviewer** (and Security) re-run at least S1, S3, S4 (two variants of their choice) and S5
in step 8 in their own scratch copy. Docker `buildx imagetools` and anonymous `git ls-remote` work in this session
(checked while planning).

- S1 — unchanged pin `v1.54.0`: exit 3, pinned row `current` with digest `sha256:0356562f…ca7c`, newest-release
  row `v1.54.1` `newer` with `sha256:3eb5379b…49b1aa` (or a higher tag if syft releases in the meantime).
- S2 — scratch pin `ghcr.io/anchore/syft:v1.54.1@sha256:3eb5379ba7b409c3f4069b686110527af0c47df993fa5c10d13e7cf34f49b1aa`:
  exit 0, one row `current` (exit 3 with a newer row if syft has released again — then note the tag).
- S3 — scratch pin `v1.54.1` with the digest of `v1.54.0`: exit 3, pinned row `stale`, AC3's warning.
- S4 — each of these scratch edits of `generate-sbom.sh` gives exit 1, one `::error::` line and no table, and runs
  no network call (run with `HTTPS_PROXY=http://127.0.0.1:9 https_proxy=http://127.0.0.1:9` so a network call
  would show as exit 4 instead): double-quoted value; unquoted value; trailing space after the closing quote;
  `readonly syft_image='…'`; `export syft_image='…'`; a second `syft_image='…'` line further down; an indented
  `  syft_image='…'` inside a function; `syft_image+='x'`; `: "${syft_image:=x}"`; `printf -v syft_image '%s' x`;
  `read -r syft_image < /dev/null`; `declare -n alias=syft_image`; a comment line naming `syft_image`; the line
  before the assignment ending in `\`; the tag `v01.54.0`; the tag `v1.54.0000000000`; the tag `latest`; an
  upper-case hex digit in the digest; another registry (`docker.io/anchore/syft:…`). Plus: the script called with
  one argument; `jq` hidden from `PATH`.
- S5 — lookup failure: the unchanged pin with `HTTPS_PROXY`/`https_proxy` pointing to `http://127.0.0.1:9`:
  exit 4, warnings naming the failed lookups, no data row.
- S6 — tag parsing against crafted tags: a local bare repository with the tags `v1.54.0`, `v1.60.0-rc.1`,
  `v01.70.0`, `v1.59.0`, `v9999999999.0.0`, `release-2.0.0` and `v1.58.10`, substituted for the syft URL only
  through the environment (`GIT_CONFIG_COUNT=1`, `GIT_CONFIG_KEY_0=url.<bare repo path>.insteadOf`,
  `GIT_CONFIG_VALUE_0=https://github.com/anchore/syft.git`): exit 4 with the warning naming
  `ghcr.io/anchore/syft:v1.59.0` as the failed lookup, which shows that `v1.59.0` was chosen as the newest release.

| Criterion | Verified by | Who, when |
| --------- | ----------- | --------- |
| AC1 | S4, S1 (a valid pin passes) | Dev step 6; Reviewer/Security step 8 |
| AC2 | S1, S6; reading the `git` call for the five settings | Dev step 6; Reviewer/Security step 8 |
| AC3 | S3, S1 | Dev step 6; Reviewer step 8 |
| AC4 | S1, S2 | Dev step 6; Reviewer step 8 |
| AC5 | S1–S6 output and exit statuses; reading the script for stray output | Dev step 6; Reviewer/Security step 8 |
| AC6 | `actionlint` in the *Analyzer gate*; reading the diff; a `workflow_dispatch` run of the branch's workflow (`gh workflow run base-image-digests.yml --ref <branch>`) after the PR is open, which with today's data opens the issue *SBOM generator pin is stale* with the S1 table and leaves the job `check` as before — the orchestrator triggers it only with the Product Manager's go-ahead, because it writes real issues; without it, the maintainer's first dispatch after the merge, its result recorded on the pull request | Orchestrator step 7 (gate), Reviewer/Security step 8, orchestrator step 11 |
| AC7 | `git diff origin/main -- .github/workflows/base-image-digests.yml` shows the job `check` unchanged except the header comment; `actionlint`; a `grep` for `${{` inside `run:` blocks | Reviewer/Security step 8 |
| AC8 | `git diff --stat origin/main...HEAD` | Orchestrator step 9, Reviewer step 8 |
| AC9 | reading the documentation against the script and the workflow | Reviewer step 8, Lead if launched in step 9 |

Step 7 for this change: *Format check* (unaffected), the *Analyzer gate* (shellcheck on the new script, the gate's
own `shelldre:S7688`/`S7679` checks, actionlint on the workflow), `bash -n` on the script, and `scope-check.py`.
`shfmt` is missing in the session: "not verified locally, SonarQube Cloud (step 11)" per `.squad/stack.md`.

## Approach

1. A new script beside the base image check, not an extension of it: `check-base-image-digests.sh` also runs in
   `ci.yml`'s *Release build check* (as a warning) and starts with the Dockerfile pinning check; the syft check
   needs neither, and mixing them would put a `github.com` Git lookup into every pull request (rejected in 0037).
   The new script follows the existing one's structure: `set -euo pipefail`, `LC_ALL=C`, tool check, validate
   every value before it reaches a command, lookups, table, exit status by precedence. Shell functions assign
   positional parameters to `local` variables (`shelldre:S7679`) and use `[[ … ]]` only (`shelldre:S7688`).
2. "Newest release" comes from `git ls-remote --tags --refs` against the public syft repository: no token, no
   rate-limited API, `git` is on every runner. The digests come from `docker buildx imagetools inspect` and `jq`,
   exactly as for the base images.
3. The report is a second job in the same workflow with its own sibling issue *SBOM generator pin is stale*, so
   its refresh pull request can close it with `Closes #n` independently of the routinely stale base image issue,
   and a failure of one check never hides the other. The report step copies the existing one with its own title and
   labels; the check step holds no token.
4. Documentation: the *what* of the release process lives in `docs/CONTRIBUTING.md` (the release process is not an
   area, `docs/areas/README.md`); the *why* is added to record 0037.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| CI | `.github/scripts/check-sbom-generator-pin.sh` | New (executable, mode 100755 like the other scripts) |
| CI | `.github/workflows/base-image-digests.yml` | Header comment; new job `sbom-generator` |
| Docs | `docs/CONTRIBUTING.md` | *SBOM generator*, *Base image digests*, *Cutting a release* |
| Squad (product description) | `.squad/project.md` | Security area 13: the scheduled check's two jobs |
| Docs | `docs/decisions/0037-…md`, `docs/ARCHITECTURE.md` | Already edited by the Lead in step 2 |

Read and final (not to be changed): `.github/scripts/generate-sbom.sh`, `.github/scripts/check-base-image-digests.sh`,
`.github/workflows/ci.yml`, `.github/workflows/release.yml`, `.github/dependabot.yml`.

## Signatures (for the Dev's skeleton)

No C# or Go API. The contracts the Dev builds to:

- `.github/scripts/check-sbom-generator-pin.sh` — no arguments; run from the repository root; needs `git`,
  `docker` with `buildx`, and `jq`; standard output: the table of AC5; standard error: `::warning::`/`::error::`
  lines and the tools' own messages; exit status 0 / 3 / 4 / 1 as in AC5. Header comment in the style of
  `check-base-image-digests.sh`, naming its user (`base-image-digests.yml`) and record 0037.
- Workflow job id `sbom-generator`, name `Check SBOM generator pin`; step ids `pin`; step output `stale=true`;
  report file `$RUNNER_TEMP/sbom-generator-pin.md`; issue title `SBOM generator pin is stale`; labels
  `dependencies`, `release` (both exist in the repository).

## Test files

None: no production or test code (see *Verification without tests*). Existing test code that calls a changed
signature: none.

## Areas

None. The change concerns the release process, which is not an area (`docs/areas/README.md`, *Rules*: "Development
and release process are not areas (see `CONTRIBUTING.md`)"); records 0037 and 0041 carry `Area: —`. The behavior is
written once in `docs/CONTRIBUTING.md`.

## Documentation updates

- `docs/CONTRIBUTING.md` — owner **Dev**:
  - *SBOM generator*: a paragraph on the weekly check: the job of the *Base image digests* workflow (Mondays and on
    manual dispatch); that it reads the constant and fails unless it is the only assignment of `syft_image` in the
    script and every other mention is an expansion; that "newest" is the highest `vX.Y.Z` tag of
    `https://github.com/anchore/syft` read with `git ls-remote` without credentials (pre-releases ignored); that it
    compares the pinned tag's index digest on `ghcr.io`; that it opens or updates the issue *SBOM generator pin is
    stale*, which the refresh pull request should close (`Closes #n`); and the local command
    `.github/scripts/check-sbom-generator-pin.sh` (needs `git`, `docker buildx` and `jq`).
  - *Base image digests*: one clause that the same workflow also checks the SBOM generator pin (*SBOM generator*).
  - *Cutting a release*: one sentence: if the issue *SBOM generator pin is stale* is open, refresh the pin first;
    a stale pin gives a less complete SBOM, it does not stop the release.
- `.squad/project.md`, security area 13 — owner **Dev**: replace "The scheduled digest check holds only
  `contents: read` and `issues: write`, writes only regex-checked image, tag and digest values into the issue, and
  never writes to the repository." by a sentence saying that each of the scheduled check's two jobs (base image
  digests, SBOM generator pin) holds only `contents: read` and `issues: write`, that its lookups (registries,
  syft's Git tags) run without a token, that it writes only regex-checked image, tag and digest values into its two
  issues, and that it never writes to the repository. Records stay "0027, 0037, 0041".
- Record 0037 — owner **Lead**, done in step 2 (see *Decision records*).
- `docs/ARCHITECTURE.md`, *Deployment*, the SBOM bullet — owner **Lead**, done in step 2: one sentence that the
  syft pin is refreshed by hand and the weekly base image digest workflow reports a newer release or a moved digest
  as an issue of its own.
- `README.md`: none.

## Architecture check

No guarantee in `.squad/project.md` (*Guarantees*) or `docs/ARCHITECTURE.md` is weakened. Security area 13 is kept
and extended to the new job: the workflow still writes nothing to the repository and opens no pull request; the new
job holds the same two permissions as the existing one; its lookup step holds no token; only constant text and
regex-checked values reach the issue; the job token is used only by the report step. The release pipeline itself
(`release.yml`, `generate-sbom.sh`) is untouched. The *Deployment* section gains one sentence (done).

## Security considerations

- [ ] Limits: no byte limit. Numeric limit: each version part has at most 9 digits (AC1, AC2), applied to the
  string before any arithmetic, so bash arithmetic cannot overflow and a leading zero cannot be read as octal. The
  `git ls-remote` output is held in memory unbounded, like the registry answers in the existing check: both come
  from `github.com`/`ghcr.io`, the sources the release pipeline already trusts.
- [ ] User-visible failure texts: only the script's fixed `::error::`/`::warning::` texts with regex-checked values
  (AC1, AC3, AC4, AC5) and the workflow's fixed `::error::` text; the issue body holds only fixed text and the table.
  `git` and `docker` print their own errors to the job log, as in the existing check.
- [ ] No token in the lookup step: the step has no `env:`; checkout does not persist the token; `git` runs with the
  five settings of AC2, so even a checkout with a persisted `http.extraheader` or a developer's credential helper
  adds nothing. Git configuration from the environment (`GIT_CONFIG_COUNT`, `GIT_CONFIG_PARAMETERS`) is still read;
  the workflow sets none, and S6 uses exactly that to test the tag parser.
- [ ] **Forms of the guarded input.** Two inputs are guarded.
  - *The pin in `generate-sbom.sh`*, as bash reads it. What the check reads must be what bash assigns. Forms bash
    accepts and the guard's answer:
    | Form | Guard |
    | ---- | ----- |
    | `syft_image='v'` at column 0, alone on its line | accepted (the only accepted form) |
    | `syft_image="v"`, `syft_image=v`, `syft_image=$'v'`, trailing text, `; cmd` after it | rejected by rule 2 |
    | `declare`/`typeset`/`local`/`readonly`/`export syft_image=v` | rejected by rule 4 |
    | a second assignment anywhere, also indented or inside a function or condition | rejected by rule 1 or 4 |
    | `syft_image+=x` (append), also at column 0 (it does not start with `syft_image=`) | rejected by rule 4 |
    | `${syft_image:=x}`, `${syft_image=x}`, `${syft_image:-x}` | rejected by rule 4 |
    | `printf -v syft_image`, `read syft_image`, `mapfile`/`readarray`, `for syft_image in`, `getopts … syft_image`, `unset syft_image`, `declare -n x=syft_image`, `eval 'syft_image=…'`, a comment naming it | rejected by rule 4 |
    | the line continued from the previous line (`\` at its end), so it is an argument or a second assignment of one command | rejected by rule 3 |
    | an environment variable `syft_image` | no effect: the unconditional top-level assignment overrides it |
    | a name built at run time (`printf -v "$name"`, `declare "$a$b=…"`, `eval "$x"`), or the assignment line inside a here-document or a multi-line string | not detected — residual risk: at most a wrong "current"; `generate-sbom.sh:71` checks the reference it really runs against `syft_re`, so no unpinned image can run. Recorded in 0037 |
    The value itself then passes `syft_re` (verbatim from `generate-sbom.sh`) and the strict tag regex (rule 5)
    before it reaches `docker` or the table.
  - *Tags from `git ls-remote --tags --refs`*. The output is lines `<object id><TAB>refs/tags/<name>`; without
    `--refs` also `<name>^{}` peeled lines (excluded by `--refs` and in any case by the regex). Git's ref-name rules
    allow almost any printable name (`v1.0.0-rc.1`, `v1.0.0+meta`, `V1.0.0`, `1.0.0`, `v01.0.0`, very long numbers,
    `/`, Unicode); the script splits each line at the first tab and accepts the rest only if it matches the whole
    regex of AC2, so every other form, an extra tab, a CR or an over-long part is ignored and can neither reach
    `docker` nor the issue. A line without a tab is ignored.
  - *Registry answers*: `jq -r .digest` output must match `^sha256:[0-9a-f]{64}$` as a whole string (several lines
    or extra text fail), as in the existing check.

## Decision records

- `docs/decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md` — **extended in place**
  (unreleased: no `v*` tag exists; status stays `Accepted`, index row unchanged): issue #124 under *Source*; the
  options for noticing a stale syft pin (same issue as the base digests, GitHub REST API, `ghcr.io` tag list, a
  `Release build check` warning — rejected; `git ls-remote` + `ghcr.io` digests in a sibling issue of a second job
  — chosen) under *Options considered*; one sentence in *Decision*; the follow-up line in *Consequences* replaced by
  the accepted limits (newest = highest Git tag; a tag without a published image fails the check until it appears;
  a back-ported patch on an older line is not reported; the literal-form reader and its residual risk). No new
  record: 0037 is the unreleased record on this topic and names this follow-up. Record 0041 stays as it is (its
  base image report is unchanged).

## Out of scope / follow-ups

- Refreshing the pin to `v1.54.1`: not in this change. The new check opens the issue *SBOM generator pin is stale*
  on its first run; the refresh is its own pull request, whose `Release build check` proves the new pin.
- Proposed follow-up issue for the related defect (the orchestrator opens it):
  - Title: `[Docker] Dependabot docker entry contradicts record 0041`
  - Body: "`.github/dependabot.yml` has a `package-ecosystem: docker` entry for `/deploy/backend` (added back in
    #135), but record 0041 says the entry was removed because Dependabot's docker parser reads neither `ARG` lines
    nor `${…}` references in `FROM` and finds nothing, and that Dependabot no longer covers `docker`. Either remove
    the entry or, if Dependabot now resolves `ARG` defaults in `FROM` lines, record that in 0041 and decide whether
    the weekly base image digest workflow is still needed. Found while planning #124."
- A syft freshness warning in `ci.yml` (*Release build check*): rejected in 0037.
