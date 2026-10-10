# Plan: Remove the Dependabot docker entry that record 0041 rules out

Source: Issue #173
Status: Draft
Tier: security — the change edits CI configuration (`.github/dependabot.yml`), which the `security` row of the tier
table in `.squad/routing.md` names ("Docker/CI or build configuration"), and it concerns how the digest-pinned base
images of security area 13 are kept current; when in doubt, the higher tier.

## Problem / root cause

`.github/dependabot.yml`, lines 37-41, holds a `package-ecosystem: docker` entry for `/deploy/backend`. Record 0041
(Accepted, unreleased: the repository has no `v*` tag) decided that this entry is removed, because Dependabot's
`docker` parser cannot read the digest-only `FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}` lines of
`deploy/backend/Dockerfile` (lines 14 and 45) and so finds nothing. The entry was removed in #107 (16d1361) and came
back unannounced in the .NET port #135 (f3ffa9c), together with the new `nuget` and `dotnet-sdk` entries. No record
decided its return: the record #135 itself added (0080, since merged into 0041 by #146) still said "if Dependabot learns
to resolve `ARG` defaults in `FROM` lines, the `docker` entry can return". The re-addition is therefore an accident of
the port, not a decision, and the entry is stale.

Claims of the issue, checked:

- "added back in #135" — confirmed: `git log --follow -- .github/dependabot.yml` shows the entry added in #102, removed
  in #107 and re-added in #135.
- "record 0041 says the entry was removed because the parser reads neither `ARG` lines nor `${…}` references in
  `FROM`" — confirmed: 0041 *Context* and *Options considered* (pinning form: "the Dependabot `docker` entry is removed
  because it finds nothing").
- "and that Dependabot no longer covers `docker`" — confirmed: 0041 *Consequences*, first sentence of the fourth bullet.
- "if Dependabot now resolves `ARG` defaults in `FROM` lines" — refuted, checked on 2026-10-10 against dependabot-core
  `main`: `docker/lib/dependabot/docker/file_parser.rb` matches each line against
  `^FROM\s+(--platform=\S+\s+)?(REGISTRY/)?IMAGE(:TAG)?(@sha256:DIGEST)?( AS NAME)?`, where `IMAGE`
  (`docker/lib/dependabot/shared/shared_file_parser.rb`) must start with `[a-z\d]`; no file of the docker ecosystem
  (`file_fetcher.rb`, `file_parser.rb`, `file_updater.rb`, `update_checker.rb`) handles `ARG` or `${`. Running that
  exact regular expression (Ruby) over `deploy/backend/Dockerfile` gives `NO MATCH` for both `FROM` lines. The
  fetcher's YAML path only reads Kubernetes resources (`apiVersion` and `kind`), which `deploy/backend/*.y*ml` are not,
  so the entry does not reach the compose file's `networlddev/vandox:latest` either (record 0060 wants no Dependabot
  entry for it).
- Consequence for the job `check` of `.github/workflows/base-image-digests.yml`: it stays needed; it is the only
  report of a stale base image digest besides the warning in the *Release build check*.

Evidence from the repository's runs: the workflow *Dependabot Updates* ran `docker in /deploy/backend` successfully on
2026-10-07 and 2026-10-10 and opened no pull request. This does not prove the parser result by itself, because both
pinned digests are current (MCR `docker-content-digest` of `dotnet/sdk:10.0` and `dotnet/aspnet:10.0-noble-chiseled`
on 2026-10-10 equal the Dockerfile's `BASE_*_DIGEST`). **Unverified:** the job log ("0 dependencies"); its download was
refused (HTTP 403 on the log blob). The parser source is the evidence the decision rests on.

Related findings on the way:

- Record 0041's fourth *Consequences* bullet read "if it learns to resolve `ARG` defaults in `FROM` lines, the entry and
  the weekly workflow's job `check` can go" — "the entry can go" presumes an entry exists, the opposite of the
  decision (the merged-away 0080 said "can return"). Fixed by the Lead in this plan step (see *Decision records*).
- The workflow *Base image digests* has no run yet (`total_count` 0): it was merged on Monday 2026-10-05 at 20:11,
  after that day's 05:00 cron, so its first scheduled run is Monday 2026-10-12. Expected, not a defect; noted for the
  orchestrator because 0041's reasoning relies on it.

## Acceptance criteria

- [ ] AC1: `.github/dependabot.yml` has no entry with `package-ecosystem: docker`; it still parses as YAML and lists
  exactly the ecosystems `github-actions`, `gomod`, `nuget`, `dotnet-sdk`, in that order, each with its current
  settings unchanged (the diff of the file removes only the docker entry and the blank line before it).
- [ ] AC2: The file ends with the `dotnet-sdk` entry (`      interval: weekly`) followed by exactly one line break,
  no trailing blank line.
- [ ] AC3: No file outside `.github/dependabot.yml`, `docs/decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md`
  and `specs/issue-173/` changes; in particular `deploy/backend/Dockerfile`, `.github/workflows/base-image-digests.yml`
  (job `check` stays), `.github/scripts/` and `docs/CONTRIBUTING.md` are untouched.
- [ ] AC4: Record 0041 matches the configuration: it states that `.github/dependabot.yml` has no `docker` entry and
  under which condition the entry may return and the job `check` go; its status and index row stay `Accepted`.

## Verification without tests

The change touches no production or test code (a Dependabot configuration file and a decision record), so steps 4
(*Skeleton*), 5 (*Tests first*) and the *Coverage gate* of step 6 are **not applicable** (`.squad/routing.md`,
*Changes without production or test code*). The tier stays `security`: plan challenge, Security plan review (step 3)
and Security diff review (step 8) run.

| AC | Where and how | Who |
| -- | ------------- | --- |
| AC1 | Read-only check on the head: `python3 -c "import yaml; d=yaml.safe_load(open('.github/dependabot.yml')); print([u['package-ecosystem'] for u in d['updates']])"` prints `['github-actions', 'gomod', 'nuget', 'dotnet-sdk']`; `git diff origin/main -- .github/dependabot.yml` shows only removed lines (the blank line and the four docker lines). | Dev runs it after the edit and reports the output; Reviewer re-reads the diff in step 8 |
| AC2 | `tail -c 20 .github/dependabot.yml \| od -c` ends in `w e e k l y \n` with no second `\n`. | Dev; Reviewer |
| AC3 | `git diff --stat origin/main...HEAD` plus `git status --porcelain` list only the three paths; `python3 .squad/tools/scope-check.py` passes. | Orchestrator (step 7); Reviewer |
| AC4 | Read 0041 *Consequences*, fourth bullet, against the file; `python3 .squad/tools/decision-check.py` passes. | Lead (already edited); Reviewer |

After the merge (step 11, informational, not a merge condition): Dependabot reruns on a change of
`.github/dependabot.yml` on the default branch, and from then on *Dependabot Updates* shows no `docker in
/deploy/backend` run (`gh api repos/larslaskowski/vandox/actions/workflows/374139427/runs`). Checked by the orchestrator.
Dependabot reads only the default branch, so this cannot be verified before the merge.

## Approach

Delete lines 37-41 of `.github/dependabot.yml` (the blank line 37 and the four lines of the docker entry):

```yaml

  - package-ecosystem: docker
    directory: "/deploy/backend"
    schedule:
      interval: weekly
```

No comment is left in the file: the reason lives in record 0041 and in `docs/CONTRIBUTING.md` (*Base image digests*:
"Dependabot cannot read these lines"), which already state it. Nothing else changes.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Repository configuration | `.github/dependabot.yml` | Remove the `docker` entry for `/deploy/backend` (Dev) |
| Decision records | `docs/decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md` | Wording and source of the parser check (Lead, done in this step) |

## Signatures (for the Dev's skeleton)

None.

## Test files

None (no production or test code). Existing test code calling a changed signature: none.

## Areas

None: Dependabot configuration is part of the development process, which `docs/areas/README.md` excludes from the
areas ("Development and release process are not areas (see `CONTRIBUTING.md`)"), and no behavior of Vandox changes.

## Documentation updates

None beyond record 0041. `docs/CONTRIBUTING.md` (*Base image digests*) already says Dependabot cannot read the
`BASE_*` lines and the digests are refreshed by hand; `docs/ARCHITECTURE.md` and `.squad/project.md` do not mention
Dependabot's docker coverage. Owner of the record edit: Lead (done).

## Architecture check

Guarantee touched: every base image pinned by digest (`docs/ARCHITECTURE.md`, release section; `.squad/project.md`,
security area 13). It is preserved and unaffected: the pinning form, the release workflow's form check, the
*Release build check* warning and the weekly job `check` all stay. The removed entry contributed nothing to keeping
the digests current (it parsed no dependency), so no update path is lost. No guarantee is weakened; the Product
Manager is not needed.

## Security considerations

- [x] Byte or length limits on parsed input: no limits (no parser changes).
- [x] Exceptions reaching a user-visible message: none.
- Removing an entry narrows what Dependabot runs with write access to pull requests; it widens nothing. The digest
  refresh path (by hand, reviewed pull request, full CI; stale report by the weekly job) is unchanged.

## Decision records

- `docs/decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md` (Accepted, unreleased, edited
  in place by the Lead in this step; decision unchanged): *Source* names #173; *Context* names the parser file and
  the re-check on 2026-10-10; the fourth *Consequences* bullet now says the file has no `docker` entry, why a present
  one is harmful (green runs, no dependency, false impression of coverage) and that only Dependabot resolving `ARG`
  defaults lets the entry return and the job `check` go. Index row unchanged (title and status still fit). No new
  record: no new choice between alternatives is made, the change restores what 0041 decided.

## Out of scope / follow-ups

- Replacing the hand refresh with automation (Renovate custom manager, a scheduled pull request): rejected in 0041,
  not reopened here.
- A guard that keeps the entry from returning by accident (e.g. a CI check on `.github/dependabot.yml`): not
  proposed; the record and this fix are the guard, and a check for one YAML key is out of proportion.
- Possible squad lesson for step 12 (orchestrator decides): #135 re-added the entry while its own record said it was
  absent — a review sweep of seeded stack configuration files (Dependabot) against accepted records could have caught it.
