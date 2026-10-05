# Log: Issue #119 Publish an SBOM for the release artifacts

One line per pipeline step: date, step, member, result. Lead decisions and escalations are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-05 | 1 Intake | Orchestrator | Issue #119 read (open, labels area: repo, type: chore, no comments); branch fix-issue-119-release-sbom off origin/main 95f00d7 |
| 2026-10-05 | 2 Plan | Lead | RESULT: DONE, tier security; plan.md and Proposed record 0056 (syft container pinned by digest, SPDX 2.3, shared script generate-sbom.sh, two extra actions/attest steps with sbom-path, ci.yml dry run); steps 4, 5 and coverage gate not applicable (no production or test code) |
| 2026-10-05 | 2 Plan challenge | Devil's Advocate | VERDICT: OBJECTIONS 0 major, 2 minor: (1) plan cites wrong release.yml line numbers (attest job is lines 238-289, permissions 241-243); (2) AC1 requires a Docker Hub digest cross-check that the script never uses and that may hit 429 |
| 2026-10-05 | 2 Plan revise | Lead | RESULT: DONE; both minor objections accepted (line numbers corrected; Docker Hub cross-check dropped, ghcr.io only); tier unchanged (security) |
| 2026-10-05 | 3 Plan security review | Security | VERDICT: APPROVED, no blocking findings; 2 non-blocking: (1) container output can emit workflow commands into the runner log (wrap docker run in ::stop-commands::, no SBOM-derived strings in ::error:: lines); (2) bind-mount syntax (absolute paths, --mount type=bind ... readonly, inputs regular non-symlink files without , or :) |
| 2026-10-05 | 3 Plan revise | Lead | RESULT: DONE; both non-blocking Security findings accepted (new AC11 log isolation via ::stop-commands::, AC4/AC5/AC2 amended for fixed-text errors and --mount with checked absolute paths); tier security. Steps 4, 5 and coverage gate skipped: no production or test code changes |
| 2026-10-05 | 6 Implement | Dev | Done: generate-sbom.sh, release.yml, ci.yml, README, CONTRIBUTING, ARCHITECTURE, .squad/project.md; syft pin v1.54.0@sha256:0356562f...; local stand-in run passed (main module github.com/LarsLaskowski/Vandox); pinned ghcr.io image could not be pulled (proxy 403), left to the PR Release build check; coverage gate n/a |
| 2026-10-05 | 7 Code check | Code Officer | No edits needed; verified by orchestrator: gofmt clean, analyzer gate PASS, go test -race green; coverage gate n/a (no Go changes) |
| 2026-10-05 | 8 Review round 1 | Reviewer, Security | Security: APPROVED, no findings. Reviewer: BLOCKING 1 (generate-sbom.sh:132-136, generator output without trailing newline leaves the resume line mid-line, so ::stop-commands:: is never resumed and the fixed ::error:: is not an annotation; fix: end each log with a newline before the next marker) |
| 2026-10-05 | 8 Fix round 1 | Dev | Blocking finding fixed: newline after each log and before the EXIT-trap resume marker in generate-sbom.sh; stub runs show the marker on its own line and the fixed ::error:: after it |
| 2026-10-05 | 7 Code check (2) | Code Officer | No edits; verified by orchestrator: gofmt clean, analyzer gate PASS, tests green |
| 2026-10-05 | 8 Review round 2 (delta) | Reviewer | VERDICT: APPROVE; blocking finding resolved, nothing new; stub reproduction shows resume marker and fixed ::error:: each on their own line |
