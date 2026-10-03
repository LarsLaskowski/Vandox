# Copilot instructions

Project guidance for GitHub Copilot when working in this repository. These rules mirror `CLAUDE.md` and `AGENTS.md`; keep all three in sync —
everything from the first `##` heading on is identical in all three files. This file is a summary; the
binding, detailed references are [`ARCHITECTURE.md`](/docs/ARCHITECTURE.md) (how the system is put
together and why), [`CONTRIBUTING.md`](/docs/CONTRIBUTING.md) (workflow, PR conventions, versioning),
[`UNIT_TESTS.md`](/docs/UNIT_TESTS.md) (test conventions — **unit tests are mandatory for new code**) and
[`.squad/stack.md`](/.squad/stack.md) (toolchain and commands). Read them before making a non-trivial
change; when this file and one of them appear to disagree, treat that as a sync bug to fix, not as
license to pick either one.

## What this project is

<!-- project:begin overview -->
Vandox is lean monitoring for a Plesk-managed Linux server, with analysis first: it reconstructs outages from
logs and system metrics and warns early. It consists of two Go binaries: `vandox-agent` runs on the monitored
server and collects metrics and logs, and `vandoxd` is the backend with web UI, which runs as a Docker
container on a Synology NAS in the home network. See [`ARCHITECTURE.md`](/docs/ARCHITECTURE.md) for how it fits together.
<!-- project:end overview -->

## Golden rules

- **Never commit or push to `main`** — no one, not even with approval. Every change goes through a
  separate branch and a pull request.
- **Commits and pushes to a feature branch are always allowed** without asking: commit finished work and
  push it to the current feature branch (creating that branch off `main` if needed), so nothing is lost
  when a session ends. Force-pushing or otherwise rewriting published history, deleting branches, and
  creating tags (a `v*` tag may trigger a release) still need explicit user approval.
- **Pull requests are only opened by the squad or by the user.** The `squad-issue` and `squad-spec`
  skills open a PR after the Lead's approval (tier `docs`: after a clean review); outside the squad, a PR
  is opened only when the user explicitly asks for one (e.g. by running the `create-pr` skill). Never open
  a PR on your own initiative.
- Run *Format* from [`.squad/stack.md`](/.squad/stack.md) after editing code and before building; CI is
  not meant to find formatting issues. In the squad skills only the Code Officer runs it.
- A changed file may not carry **any analyzer diagnostic of any severity**, including info-level ones that
  never show up as build warnings but that the CI code analysis (e.g. SonarQube Cloud) reports. Check with
  the *Analyzer gate* from `.squad/stack.md` and fix every finding before considering the work done (in
  the squad skills, the Code Officer owns this).
- New or changed production code needs **at least 80 % line coverage**, and overall coverage must stay
  at least 80 % (*Coverage gate* in `.squad/stack.md`, see [`UNIT_TESTS.md`](/docs/UNIT_TESTS.md#code-coverage)).
<!-- stack:begin golden-rules -->
- `gofmt` formats everything; `go vet` and **golangci-lint** must report nothing new, and `govulncheck`
  must stay clean.
- Add dependencies with `go get`, run `go mod tidy`, and commit `go.mod` and `go.sum` together.
<!-- stack:end golden-rules -->

## Commit messages

- Keep the subject line to a single summary of **no more than 80 characters** and do not end it with a
  period.
- Do not write the message in the first person.
- Keep the body to **3–5 sentences**, depending on the number of changes.

## Pull requests

- Title and description are always written in **English**, regardless of the language used in the
  conversation.

## Commands

<!-- stack:begin commands -->
```bash
go mod download
gofmt -w .
go build ./...
go vet ./...
go test ./... -race -coverprofile=coverage.out
python3 .squad/tools/analyzer-check.py                      # analyzer gate (vet + golangci-lint)
python3 .squad/tools/coverage-check.py                      # coverage gate
```
<!-- stack:end commands -->

All commands, with what each one checks, are listed in [`.squad/stack.md`](/.squad/stack.md).

## Architecture

<!-- project:begin architecture -->
- `cmd/vandox-agent` — the agent for the monitored server (collectors, log shipping, local spool).
- `cmd/vandoxd` — the backend with web UI (ingest, storage, analysis, alerting).
- `internal/` — packages shared by both binaries (data model, log parsing, signatures, version information).
- `deploy/agent`, `deploy/backend` — installation and container files; `docs/` — documentation; `testdata/` — test fixtures.
<!-- project:end architecture -->

## Project configuration

<!-- stack:begin configuration -->
- **Go** version and module path from `go.mod`; tools (`govulncheck`) as `tool` dependencies in `go.mod`.
- **golangci-lint** configured in `.golangci.yml`.
<!-- stack:end configuration -->
<!-- project:begin configuration -->
<!-- project:end configuration -->

## Code style

<!-- stack:begin code-style -->
`gofmt` formatting; short lower-case package names; doc comments on exported identifiers starting with
the identifier's name; errors returned and wrapped with `%w`, never ignored; no `panic` in library code;
`context.Context` first for anything that does I/O or blocks.
<!-- stack:end code-style -->
<!-- project:begin code-style -->
<!-- project:end code-style -->

## Testing

<!-- stack:begin testing -->
**Unit tests are mandatory for newly written code.** Standard `testing` package, colocated `_test.go`
files, table-driven tests with `t.Run`, `t.Helper()` in helpers, `t.TempDir()` for files, failure messages
that state got and want; no real network or clock.
<!-- stack:end testing -->
Full conventions, including the project's test doubles and the checklist to run before committing a new
test, are in [`UNIT_TESTS.md`](/docs/UNIT_TESTS.md).

## Related skills

Project-specific workflow skills live under `.claude/skills/`, mirrored identically under
`.agents/skills/` (Codex/GPT) and `.github/skills/` (GitHub Copilot):

- `create-pr` — verify (format, build, tests, analyzer and coverage gates), review the change locally,
  then open a PR following [`.github/pull_request_template.md`](/.github/pull_request_template.md).
- `squad-issue` — fix a GitHub issue with the squad: the Lead plans and picks a tier
  (`docs` / `trivial` / `standard` / `security`), the Devil's Advocate challenges `standard`/`security`
  plans once, Security reviews security-relevant plans, the Tester writes failing tests first, the Dev
  implements to ≥ 80 % coverage, the Code Officer clears format and analyzer diagnostics, Reviewer and
  Security review the diff, the Lead approves, then a PR referencing the issue is opened.
- `squad-spec` — the same squad pipeline for a new feature, planned as `spec.md`, `plan.md` and
  `tasks.md` in a working folder under `specs/`.
- `review-pr` — review an open pull request against this project's stack, analyzer, security and
  unit-test conventions, and post the findings with an explicit verdict.

Review runs as a subagent defined in `.claude/agents/squad-reviewer.md` (read-only, pinned to Opus, fresh
context). `create-pr` and the squad skills call it *before* pushing, so a change is reviewed while it is
still local; `review-pr` calls the same agent for a pull request that is already open. The review
checklist, the integration-surface sweep, the blocking/non-blocking severity model and the "round 1 is a
full review, later rounds review only the delta" rule live in that one file, so they are identical either
way. An agent without subagent support follows the same file inline.

The squad skills run a multi-role pipeline defined in [`.squad/`](/.squad/team.md) — Lead (plan, decisions,
PR approval), Devil's Advocate (one plan challenge), Security (plan and diff), Tester (tests first,
coverage), Dev, Code Officer (format, analyzers) and Reviewer — as subagents under
`.claude/agents/squad-*.md`, with the loop limits and escalation rules in
[`.squad/routing.md`](/.squad/routing.md). Stack commands live in [`.squad/stack.md`](/.squad/stack.md),
the project's guarantees, security areas and integration surface in
[`.squad/project.md`](/.squad/project.md). Their working records (`plan.md`, `log.md`, for features also
`spec.md` and `tasks.md`) live under `specs/` on the work branch only; before the PR they are posted as a
comment on the issue and removed, so `main` keeps no working records. An issue or feature PR never changes
the squad or these instructions (`.squad/` except `stack.md` and `project.md`, `.claude/`,
`.github/skills/`, `.agents/skills/`, `CLAUDE.md`, `AGENTS.md`, `.github/copilot-instructions.md`): squad
lessons are filed as GitHub issues labelled `squad` and never fixed in a product PR. The squad and these
rules come from the template repository named in `.squad/template.json`: a lesson about a template-managed
file becomes an issue there and is rolled out with its `adopt-template` skill; a lesson about project
knowledge (`.squad/stack.md`, `.squad/project.md`, a project block) becomes an issue here and is worked in
a squad-maintenance PR checked with `python3 .squad/tools/config-check.py` (`.squad/routing.md`,
*Squad lessons*). The user acts as Product Manager
and is only asked when the Lead escalates. Pull requests are merged with *Squash and merge*, so only the
PR title and description reach `main`.

The reasoning behind code decisions — why something was built the way it was — is recorded by the Lead
as one decision record per decision in [`docs/decisions/`](/docs/decisions/README.md) (append-only,
superseded rather than rewritten), not in `ARCHITECTURE.md`. Read the relevant records before changing
code they cover, and do not contradict an accepted record without superseding it.

Two rules these skills enforce that are easy to get wrong:

- **A pull request documents the change, not how it was produced.** The internal review loop — its
  pass count, its findings, the commits that resolved them — never appears in the PR title, body or
  commit messages.
- **A finding posted as a review comment gets worked in that pull request**, blocking or not. It is
  never deferred to "the next change that touches this code": no such change is scheduled, and the
  session holding the context to act on it will not exist later. If it really should not be fixed
  here, reply with the reason or open a linked issue now — then resolve the thread.

## Pull requests, contributing and architecture

Follow [`CONTRIBUTING.md`](/docs/CONTRIBUTING.md) for branch/PR naming (`[area] Description`), the PR
checklist in [`.github/pull_request_template.md`](/.github/pull_request_template.md), and the
stability policy. Consult [`ARCHITECTURE.md`](/docs/ARCHITECTURE.md) before changing the behavior it
describes — the guarantees listed in [`.squad/project.md`](/.squad/project.md) are deliberate, not
incidental behavior.
