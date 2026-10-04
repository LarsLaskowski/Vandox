# Plan: Complete the CI workflows for the monorepo

Source: Issue #8
Status: Draft
Tier: security — the change edits only CI and Dependabot configuration (`.github/workflows/ci.yml`,
`.github/dependabot.yml`), and *Tiers* in `.squad/routing.md` puts every Docker/CI or build configuration
change into `security`.

## Problem / root cause

The template seeded `.github/workflows/ci.yml`, `.github/workflows/codeql.yml` and `.github/dependabot.yml`.
Each requirement of the issue, checked against those files and the repository (2026-10-04):

| Requirement / claim | Finding | Status |
| ------------------- | ------- | ------ |
| Build for all packages, both binaries | `ci.yml` job `build-test-lint`, step *Build* runs `go build ./...`, which builds `cmd/vandox-agent`, `cmd/vandoxd` and `internal/...` | already met |
| Test with `-race` and coverage for all packages | step *Test* runs `go test ./... -race -cover` (prints percentages, writes no profile); the `sonarqube` job runs `go test ./... -race -coverprofile=coverage.out` only to feed SonarQube | met for `-race`; the coverage profile the gate needs is not produced in the gating job |
| Format check as configured in `.squad/stack.md` | no step runs *Format check* (`test -z "$(gofmt -l .)"`). `.golangci.yml` enables the `gofmt` formatter, so golangci-lint would report an unformatted file, but only as a lint finding, not as the named check | **missing** |
| `go vet` | step *Vet* runs `go vet ./...` | already met |
| golangci-lint | step *Lint*, `golangci/golangci-lint-action` pinned by SHA, `version: v2.13.1` = `.squad/stack.md` *Toolchain* | already met |
| govulncheck | job `govulncheck` runs `go tool govulncheck ./...` (tool dependency in `go.mod`) | already met |
| Coverage gate as defined by the template | the template defines *Coverage gate* as `python3 .squad/tools/coverage-check.py` (≥ 80 % on new/changed lines and overall, against `origin/main`). CI never runs it; decision 0001 deliberately kept it local-only (option 3) and made SonarQube Cloud the CI system of record | **missing**, and adding it changes 0001 → superseded by 0035 |
| CodeQL analysis for Go | `codeql.yml` (advanced setup, `language: go`, `build-mode: autobuild`, actions pinned by SHA) runs on `push`/`pull_request` to `main` and weekly; recent runs on `main` and on PR branches all `success`; default setup is `not-configured`, so there is no conflict | already met — no change |
| Dependabot for `gomod`, `docker`, `github-actions` | `dependabot.yml` has `github-actions` and `gomod` (both weekly, grouped); **no `docker` entry**. The repository has no Dockerfile yet (`deploy/backend/` holds only `.gitkeep`); issue #13 adds it | **missing** `docker` → decision 0036 |
| "CI is green on every PR" | the last CI and CodeQL runs on `main` and on PR branches are all `success`; locally on this branch *Format check* passes and `go test ./... -race` passes with 88.9 % statement coverage, so adding the two gates does not turn CI red | confirmed |

Related observations (not fixed here, see *Out of scope*):
- The `sonarqube` job fails for a pull request from a fork (no `SONAR_TOKEN`); only Dependabot is excluded.
- Tests run twice per CI run (`build-test-lint` and `sonarqube`); acceptable, the Sonar job is skipped for
  Dependabot and must stay independent of it.

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

- [ ] AC1: In `.github/workflows/ci.yml`, job `build-test-lint`, the checkout step uses `fetch-depth: 0` (so
  `origin/main` exists for the coverage gate on `pull_request` and `push`).
- [ ] AC2: Job `build-test-lint` has a step *Format check*, after `setup-go` and before *Build*, that fails
  when `gofmt -l .` prints anything and then lists the offending files (same condition as *Format check* in
  `.squad/stack.md`).
- [ ] AC3: Step *Test* runs exactly `go test ./... -race -coverprofile=coverage.out` (*Test with coverage*).
- [ ] AC4: A step *Coverage gate* runs `python3 .squad/tools/coverage-check.py` after *Test*, with no extra
  arguments and without `continue-on-error`, so a value below 80 % fails the job.
- [ ] AC5: Steps *Build* (`go build ./...`), *Vet* (`go vet ./...`), *Lint* (golangci-lint `v2.13.1`), the jobs
  `sonarqube` and `govulncheck`, the triggers and `permissions: contents: read` are unchanged.
- [ ] AC6: `.github/workflows/codeql.yml` is unchanged.
- [ ] AC7: `.github/dependabot.yml` has a third entry `package-ecosystem: docker`, `directory: "/deploy/backend"`,
  `schedule.interval: weekly`, `open-pull-requests-limit: 10`, one group `docker-images` with pattern `"*"`;
  the `github-actions` and `gomod` entries are unchanged.
- [ ] AC8: No new action is introduced; every `uses:` stays pinned by full commit SHA with its version comment.
- [ ] AC9: Locally on the branch, in CI order, these pass: *Format check*, *Build*, `go vet ./...`,
  *Test with coverage*, *Coverage gate*, *Analyzer gate*. The Dev also shows once, in a scratch
  `git worktree` (never the working tree), that the new format step fails on a deliberately unformatted
  `.go` file, by running the step's shell script there.
- [ ] AC10 (step 11): the PR's CI run (including *Format check* and *Coverage gate*) and CodeQL run are green.
- [ ] AC11: `docs/UNIT_TESTS.md` and decision records 0035, 0036 are updated as listed below.

## Approach

`ci.yml`, job `build-test-lint` only — target shape:

```yaml
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          fetch-depth: 0 # the coverage gate diffs against origin/main

      - uses: actions/setup-go@... (unchanged)

      - name: Format check
        run: |
          unformatted="$(gofmt -l .)"
          if [ -n "$unformatted" ]; then
            echo "::error::gofmt -l reports unformatted files; run gofmt -w ."
            echo "$unformatted"
            exit 1
          fi

      - name: Build            # unchanged
      - name: Vet              # unchanged

      - name: Test
        run: go test ./... -race -coverprofile=coverage.out

      - name: Coverage gate
        run: python3 .squad/tools/coverage-check.py

      - name: Lint             # unchanged
```

Why this works in every trigger: with `fetch-depth: 0`, `actions/checkout` fetches all branches, so
`origin/main` exists. On `pull_request` it checks out the merge commit, whose merge base with `origin/main`
is the tip of `main`, so the gate measures exactly the PR's changed lines. On `push` to `main` and on
`schedule`, `HEAD` equals `origin/main`, the diff is empty and the script only reports overall coverage
(exit 0, by its own documented rule). `ubuntu-latest` ships `python3`; the script uses only the standard
library and reads `.squad/tools/squad_settings.py` (format `go`, report `coverage.out`).

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
| CI | `.github/workflows/ci.yml` | job `build-test-lint`: `fetch-depth: 0`, new *Format check* step, *Test* writes `coverage.out`, new *Coverage gate* step |
| CI | `.github/dependabot.yml` | new `docker` entry for `/deploy/backend` |
| CI | `.github/workflows/codeql.yml` | none (verified sufficient) |
| Docs | `docs/UNIT_TESTS.md` | one sentence, see below |
| Records | `docs/decisions/0035-…`, `0036-…` | new, `Proposed` |

## Signatures (for the Dev's skeleton)

None — no Go code changes; step 4 is skipped.

## Test files

None: the change contains no production or test code, so steps 4 and 5 are skipped and the *Coverage gate*
result cannot change (`.squad/routing.md`, *Pipeline*). Existing test code calling a changed signature: none.

## Documentation updates

Made by the Dev:
- `docs/UNIT_TESTS.md`, section *Code coverage*, after the sentence "Check it locally before a push with
  *Test with coverage* and the *Coverage gate* from [`.squad/stack.md`](../.squad/stack.md).", add:
  "CI runs the same gate on every pull request and push to `main` (`.github/workflows/ci.yml`), so a pull
  request below the threshold fails its check ([decision 0035](decisions/0035-format-check-and-coverage-gate-also-in-ci.md))."

Not changed (checked):
- `docs/CONTRIBUTING.md` *Quality gates* and `docs/ARCHITECTURE.md` *Development process* are template text
  outside the project blocks and stay true (local gates first; they do not claim CI omits them).
- `README.md` has no CI section; `.squad/stack.md` stays true (*Known pitfalls* already names `ci.yml` as the
  golangci-lint version reference); `.squad/project.md` names no CI surface.
- `.github/pull_request_template.md` checklist stays true.

At approval the Lead sets 0001 to `Superseded by 0035`, sets 0035 and 0036 to `Accepted` and indexes them.

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is touched: no runtime code, deployment file
or security-area code changes. The development-process flow gains two CI checks that already run locally.

## Security considerations

- `permissions: contents: read` stays the workflow-wide token scope; no new secret, no new third-party action,
  every action stays SHA-pinned (supply-chain).
- `fetch-depth: 0` exposes only the repository's own history to the job; `persist-credentials` stays the
  checkout default as today (read-only token, no write step follows).
- The coverage gate runs repository code (`.squad/tools/coverage-check.py`) on `pull_request` with the
  read-only token and no secrets — the same trust level as `go test` already has; no `pull_request_target`.
  The script takes no user-supplied arguments (base ref, report path and pathspecs come from
  `squad_settings.py`).
- The Dependabot `docker` entry lets Dependabot propose base-image (digest) updates for the Dockerfile #13
  adds — it improves patching of the container image; Dependabot PRs still run CI and CodeQL.

## Decision records

- `docs/decisions/0035-format-check-and-coverage-gate-also-in-ci.md` (Proposed, supersedes 0001)
- `docs/decisions/0036-dependabot-docker-entry-before-the-dockerfile-exists.md` (Proposed)

## Out of scope / follow-ups

- CodeQL: no change needed.
- Cross-compilation / static (`CGO_ENABLED=0`) builds and the image build belong to #9 (release pipeline) and
  #13 (backend container).
- A `docker-compose` Dependabot ecosystem for `deploy/backend/docker-compose.yml` is decided with #13, which
  creates that file.
- Proposed note for the orchestrator to post on issue #13 (after this PR merges): "The Dependabot `docker`
  entry added for #8 watches `/deploy/backend` (decision 0036). Place the Dockerfile there, or move the
  `directory` in `.github/dependabot.yml` in the same PR."
- `sonarqube` job on fork pull requests (no secret): not a requirement of #8 and no fork contributions exist;
  noted only.
