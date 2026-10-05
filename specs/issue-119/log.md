# Log: Issue #119 Publish an SBOM for the release artifacts

One line per pipeline step: date, step, member, result. Lead decisions and escalations are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-05 | 1 Intake | Orchestrator | Issue #119 read (open, labels area: repo, type: chore, no comments); branch fix-issue-119-release-sbom off origin/main 95f00d7 |
| 2026-10-05 | 2 Plan | Lead | RESULT: DONE, tier security; plan.md and Proposed record 0056 (syft container pinned by digest, SPDX 2.3, shared script generate-sbom.sh, two extra actions/attest steps with sbom-path, ci.yml dry run); steps 4, 5 and coverage gate not applicable (no production or test code) |
| 2026-10-05 | 2 Plan challenge | Devil's Advocate | VERDICT: OBJECTIONS 0 major, 2 minor: (1) plan cites wrong release.yml line numbers (attest job is lines 238-289, permissions 241-243); (2) AC1 requires a Docker Hub digest cross-check that the script never uses and that may hit 429 |
| 2026-10-05 | 2 Plan revise | Lead | RESULT: DONE; both minor objections accepted (line numbers corrected; Docker Hub cross-check dropped, ghcr.io only); tier unchanged (security) |
| 2026-10-05 | 3 Plan security review | Security | VERDICT: APPROVED, no blocking findings; 2 non-blocking: (1) container output can emit workflow commands into the runner log (wrap docker run in ::stop-commands::, no SBOM-derived strings in ::error:: lines); (2) bind-mount syntax (absolute paths, --mount type=bind ... readonly, inputs regular non-symlink files without , or :) |
