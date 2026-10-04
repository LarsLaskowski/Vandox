# Plan: Complete the CI workflows for the monorepo

Source: Issue #8
Status: Draft (revised after the plan challenge)
Tier: security — the change edits only CI and Dependabot configuration (`.github/workflows/ci.yml`,
`.github/dependabot.yml`), and *Tiers* in `.squad/routing.md` puts every Docker/CI or build configuration
change into `security`.

## Problem / root cause

The template seeded `.github/workflows/ci.yml`, `.github/workflows/codeql.yml` and `.github/dependabot.yml`.
Each requirement of the issue, checked against those files and the repository (2026-10-04):

| Requirement / claim | Finding | Status |
| ------------------- | ------- | ------ |
| Build for all packages, both binaries | `ci.yml` job `build-test-lint`, step *Build* runs `go build ./...`, which builds `cmd/vandox-agent`, `cmd/vandoxd` and `internal/...` | already met |
| Test with `-race` and coverage for all packages | step *Test* runs `go test ./... -race -cover`; the `sonarqube` job runs `go test ./... -race -coverprofile=coverage.out` and sends the profile to SonarQube Cloud (`sonar.go.coverage.reportPaths=coverage.out`) | already met |
| Format check as configured in `.squad/stack.md` | no step runs *Format check* (`test -z "$(gofmt -l .)"`). `.golangci.yml` enables the `gofmt` formatter, so golangci-lint reports an unformatted file, but only as a lint finding, not as the named check | **missing** → decision 0035 |
| `go vet` | step *Vet* runs `go vet ./...` | already met |
| golangci-lint | step *Lint*, `golangci/golangci-lint-action` pinned by SHA, `version: v2.13.1` = `.squad/stack.md` *Toolchain* | already met |
| govulncheck | job `govulncheck` runs `go tool govulncheck ./...` (tool dependency in `go.mod`) | already met |
| Coverage gate as defined by the template | the template defines it in `.squad/stack.md` (`coverage-check.py`) **and** in decision 0001 (source: the template): run locally before the push; in CI SonarQube Cloud, fed by the `sonarqube` job's `coverage.out`, is the system of record. Read that way the requirement is already met; running the script in CI instead would supersede 0001 and make Lead-accepted gaps (0033, 0034) red, against the issue's own criterion "CI is green on every PR" | already met (0001 + SonarQube) — no CI step added, see 0035 and *Challenge* |
| CodeQL analysis for Go | `codeql.yml` (advanced setup, `language: go`, `build-mode: autobuild`, actions pinned by SHA) runs on `push`/`pull_request` to `main` and weekly; recent runs on `main` and on PR branches all `success`; default setup is `not-configured`, so there is no conflict | already met — no change |
| Dependabot for `gomod`, `docker`, `github-actions` | `dependabot.yml` has `github-actions` and `gomod` (both weekly, grouped); **no `docker` entry**. The repository has no Dockerfile yet (`deploy/backend/` holds only `.gitkeep`); issue #13 adds it | **missing** `docker` → decision 0036 |
| "CI is green on every PR" | the last CI and CodeQL runs on `main` and on PR branches are all `success`. Locally on this branch *Format check* passes, so the new step does not turn CI red. Coverage (for the record, not gated in CI by this change): 88.9 % of statements (`go tool cover -func`), 81.8 % of lines (27/33) by the gate's line count | confirmed |

Related observations (not fixed here, see *Out of scope*):
- The `sonarqube` job fails for a pull request from a fork (no `SONAR_TOKEN`); only Dependabot is excluded.
- Overall line coverage on `main` is 81.8 %, only 1.8 points above the threshold; each further accepted
  `main` body (0034) lowers it. Tracked by the local gate and SonarQube, not by this change.

**Push restriction (flag for the orchestrator):** this change edits `.github/workflows/ci.yml`. GitHub
rejects a push that creates or updates a workflow file unless the credential has the `workflow` scope (OAuth
/ PAT) or `workflows: write` (GitHub App). Push the first commit that touches `ci.yml` as early as possible
(the Dev's step-6 commit) and check that it is accepted; if it is rejected, stop and ask the user to push
that commit or grant the permission — do not drop the `ci.yml` change or move it elsewhere.
`.github/dependabot.yml` is not a workflow file and is not affected.

## Acceptance criteria

There is no Go code in this change, so there are no unit tests (steps 4 and 5 are skipped). Each criterion is
checked by the Reviewer/Security against the diff, by the Dev running the listed commands locally, and after
the PR by the CI runs (step 11).

- [ ] AC1: Job `build-test-lint` in `.github/workflows/ci.yml` has a step *Format check*, after `setup-go` and
  before *Build*, that fails when `gofmt -l .` prints anything and then lists the offending files (same
  condition as *Format check* in `.squad/stack.md`).
- [ ] AC2: Everything else in `ci.yml` is unchanged: the checkout step (no `fetch-depth` added), *Build*
  (`go build ./...`), *Vet* (`go vet ./...`), *Test* (`go test ./... -race -cover`), *Lint* (golangci-lint
  `v2.13.1`), the jobs `sonarqube` and `govulncheck`, the triggers and `permissions: contents: read`. No
  step runs `coverage-check.py`.
- [ ] AC3: `.github/workflows/codeql.yml` is unchanged.
- [ ] AC4: `.github/dependabot.yml` has a third entry `package-ecosystem: docker`, `directory: "/deploy/backend"`,
  `schedule.interval: weekly`, `open-pull-requests-limit: 10`, one group `docker-images` with pattern `"*"`;
  the `github-actions` and `gomod` entries are unchanged.
- [ ] AC5: No new action is introduced; every `uses:` stays pinned by full commit SHA with its version comment.
- [ ] AC6: Locally on the branch, in CI order, these pass: *Format check*, *Build*, `go vet ./...`,
  `go test ./... -race -cover`, *Analyzer gate*. The Dev also shows once, in a scratch `git worktree` (never
  the working tree), that the new format step fails on a deliberately unformatted `.go` file, by running the
  step's shell script there.
- [ ] AC7 (step 11): the PR's CI run (including *Format check*), the SonarQube Cloud quality gate and the
  CodeQL run are green.
- [ ] AC8: Decision records 0035 and 0036 match what was built; the superseded draft
  `docs/decisions/0035-format-check-and-coverage-gate-also-in-ci.md` is removed (see *Decision records*);
  0001 stays `Accepted` and unchanged.

## Approach

`ci.yml`, job `build-test-lint` only — insert between `setup-go` and *Build*:

```yaml
      - name: Format check
        run: |
          unformatted="$(gofmt -l .)"
          if [ -n "$unformatted" ]; then
            echo "::error::gofmt -l reports unformatted files; run gofmt -w ."
            echo "$unformatted"
            exit 1
          fi
```

`dependabot.yml` — append:

```yaml
  - package-ecosystem: docker
    directory: "/deploy/backend"
    schedule:
      interval: weekly
    open-pull-requests-limit: 10
    groups:
      docker-images:
        patterns:
          - "*"
```

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| CI | `.github/workflows/ci.yml` | job `build-test-lint`: new *Format check* step |
| CI | `.github/dependabot.yml` | new `docker` entry for `/deploy/backend` |
| CI | `.github/workflows/codeql.yml` | none (verified sufficient) |
| Records | `docs/decisions/0035-format-check-step-in-ci-coverage-gate-stays-local.md`, `0036-…` | new, `Proposed` |
| Records | `docs/decisions/0035-format-check-and-coverage-gate-also-in-ci.md` | superseded draft, never accepted or indexed — delete |

## Signatures (for the Dev's skeleton)

None — no Go code changes; step 4 is skipped.

## Test files

None: the change contains no production or test code, so steps 4 and 5 are skipped and the *Coverage gate*
is not run (`.squad/routing.md`, *Pipeline*). Existing test code calling a changed signature: none.

## Documentation updates

None. Checked:
- `docs/UNIT_TESTS.md` *Code coverage* already says to check the gate locally before a push and names
  SonarQube's "coverage on new code" as the same measure — true as is (the sentence the first draft added
  is dropped with the CI coverage step).
- `docs/CONTRIBUTING.md` *Quality gates* and `docs/ARCHITECTURE.md` *Development process* describe local
  gates first; they stay true and do not claim CI omits the format check.
- `README.md` has no CI section; `.squad/stack.md` stays true (*Known pitfalls* already names `ci.yml` as the
  golangci-lint version reference); `.squad/project.md` names no CI surface.
- `.github/pull_request_template.md` checklist stays true.

At approval the Lead sets 0035 and 0036 to `Accepted` and indexes them; 0001 is not touched.

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is touched: no runtime code, deployment file
or security-area code changes. Accepted record 0001 is followed, not superseded: the coverage gate stays
local, CI gains an explicit form of a format check it already runs through golangci-lint.

## Security considerations

- `permissions: contents: read` stays the workflow-wide token scope; no new secret, no new third-party action,
  every action stays SHA-pinned (supply-chain).
- The *Format check* step runs only the toolchain's `gofmt` on the checked-out tree with the read-only token
  and no secrets — the same trust level as *Build* and `go test`; no `pull_request_target`. It takes no
  user-controlled input in its shell script (file names are only echoed after the check has failed).
- The checkout stays shallow (no `fetch-depth` change), so the job sees no more history than today.
- The Dependabot `docker` entry lets Dependabot propose base-image (digest) updates for the Dockerfile #13
  adds — it improves patching of the container image; Dependabot PRs still run CI and CodeQL.

## Decision records

- `docs/decisions/0035-format-check-step-in-ci-coverage-gate-stays-local.md` (Proposed, supersedes nothing)
- `docs/decisions/0036-dependabot-docker-entry-before-the-dockerfile-exists.md` (Proposed, unchanged by the
  challenge)
- The first draft `docs/decisions/0035-format-check-and-coverage-gate-also-in-ci.md` is withdrawn: it was
  never accepted or indexed, so it is deleted rather than superseded. The Lead may not run Git write
  operations; the **orchestrator** removes it (`git rm`) in the commit that records this revision. Its
  rejected option is kept as option 2 of the new 0035.

## Out of scope / follow-ups

- CodeQL: no change needed.
- A CI run of `coverage-check.py`: rejected here (0035); 0001's remedy (a new record superseding 0001)
  applies if unchecked coverage reaches `main`.
- Cross-compilation / static (`CGO_ENABLED=0`) builds and the image build belong to #9 (release pipeline) and
  #13 (backend container).
- A `docker-compose` Dependabot ecosystem for `deploy/backend/docker-compose.yml` is decided with #13, which
  creates that file.
- Proposed note for the orchestrator to post on issue #13 (after this PR merges): "The Dependabot `docker`
  entry added for #8 watches `/deploy/backend` (decision 0036). Place the Dockerfile there, or move the
  `directory` in `.github/dependabot.yml` in the same PR."
- `sonarqube` job on fork pull requests (no secret): not a requirement of #8 and no fork contributions exist;
  noted only.

## Challenge

Devil's Advocate, one round (2026-10-04). Every objection answered:

1. **MAJOR — "Coverage gate as defined by the template" is met by 0001 + SonarQube; superseding 0001 was
   not justified.** **Accepted; plan revised.** The template defines the gate in `.squad/stack.md` *and* in
   0001 (source: the template), which places it before the push and makes SonarQube Cloud the CI system of
   record; the `sonarqube` job already feeds `coverage.out` to it. The first draft dismissed that reading
   without showing why it fails the issue — it does not. It is also the only reading consistent with the
   issue's own criterion "CI is green on every PR" (see 2). Since this reading follows an accepted record,
   the issue's text and its acceptance criteria, it is not ambiguous enough to need the Product Manager and
   breaks no guarantee, so no escalation. Revised: no `coverage-check.py` step, no `fetch-depth: 0`, *Test*
   unchanged, no `docs/UNIT_TESTS.md` edit, 0001 not superseded; 0035 rewritten (new file, old draft
   deleted) with the CI coverage step as rejected option 2. The *Format check* step and the Dependabot
   `docker` entry stay.
2. **MINOR — a red-by-design CI coverage check conflicts with the step-11 exit criterion "CI green".**
   **Accepted; resolved by 1.** With no CI coverage step there is no conflict, and Lead-accepted gaps
   (0033, 0034) do not turn CI red. The conflict is named in 0035 as a reason for rejecting option 2, so no
   template lesson is needed.
3. **MINOR — the "CI stays green" evidence cited statement coverage, the gate measures lines (81.8 %,
   27/33).** **Accepted; plan revised.** The table now gives both figures and names the gate's line count;
   the 1.8-point margin is listed under *Related observations* and in 0035's context. It no longer affects
   CI's result, since CI does not run the script.
