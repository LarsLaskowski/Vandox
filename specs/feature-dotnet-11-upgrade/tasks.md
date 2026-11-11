# Tasks: Move the backend to .NET 11

Plan: [plan.md](plan.md)

Each task names its criteria, the tasks it waits for and its group (steps 4–6 run per group, one agent per member
and mode; rows consecutive). A group has a "Skeleton" row for its API in *Signatures*, or says "no new API". Each
documentation edit is in one row, owned by the Dev (test documentation: the Tester; an edit under `.squad/`: the
Orchestrator, from the text its member reports), in the last group. Tasks of the Product Manager and the Lead alone
(a probe, a survey run, a fact correction) go to group `PM`, which steps 4–6 skip.

| # | Group | Task | Files | Criteria | Depends on | Owner | Done |
| - | ----- | ---- | ----- | -------- | ---------- | ----- | ---- |
| 1 | G1 | Skeleton (`RenderAsync`, `Dispose`): platform move (SDK, target framework, packages incl. Sonar), `.editorconfig` lines, Dockerfile tags and current digests, every mechanical fix of *Verified facts* 1 and 3 in `src/`, `CancellationToken.None` writes; `RenderAsync` returns `Task<bool>` (today's body, `return true`), `SessionCircuitHandler : IDisposable` with an empty `Dispose`, skeleton pragmas for BL0016/BL0013 | `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `deploy/backend/Dockerfile`, `src/**` of plan *Affected projects and types* | AC1, AC2, AC3, AC7 | — | Dev | [ ] |
| 2 | G1 | Test-code diagnostics (CA2026, MSTEST0049/S8949), `LockedTextWriter` span overrides, doubles `ChangeUser`, `UnsetAuthenticationStateProvider`, `CancellingTextReader` | `tests/Vandox.Backend.Tests/*`, `tests/Vandox.Core.Tests/*` of *Verified facts* 2 | AC1, AC2 | 1 | Tester | [ ] |
| 3 | G1 | Tests first: AC5, AC6, AC7, AC9 tests of *Tests per criterion* | test files of plan *Test files* | AC5, AC6, AC7, AC9 | 2 | Tester | [ ] |
| 4 | G1 | Implement the session subscription and the chart's disconnect handling, remove the skeleton pragmas; *Test with coverage*, *Coverage gate* | `SessionCircuitHandler.cs`, `ChartInterop.cs`, `TimeSeriesChart.razor.cs` | AC5, AC6 | 3 | Dev | [ ] |
| 5 | G1 | *Image build check* (restore-order reproduction), `check-builder-dotnet-version.sh`, `check-base-image-pinning.sh` | — | AC3 | 4 | Dev | [ ] |
| 6 | G2 | No new API. Documentation: `README.md`, `AGENTS.md` and `.github/copilot-instructions.md` project blocks, `docs/ARCHITECTURE.md`, `docs/CONTRIBUTING.md`, `.github/dependabot.yml`, Dockerfile comment, area documents web-login, web-ui, backend-host | plan *Documentation updates*, *Areas* | AC8, AC9, AC6 | 4 | Dev | [ ] |
| 7 | G2 | Report exact text for `.squad/stack.md` and `.squad/project.md` (platform lines) | `.squad/stack.md`, `.squad/project.md` | AC8 | 4 | Dev → Orchestrator | [ ] |
| 8 | G2 | Report exact text for `.squad/project.md` *Test doubles* (web login row: `Unset*` providers) | `.squad/project.md` | AC5 | 3 | Tester → Orchestrator | [ ] |
| 9 | PM | Early draft PR after step 7; build the image of the green head and run the manual check on the DS918+ (plan *Approach*); log the sign-off with the head commit | — | AC4 | 5, 6, 7, 8 | Product Manager | [ ] |
| 10 | PM | Set 0073 to `Superseded by 0197` when 0197 is accepted | `docs/decisions/0073-*.md` | AC8 | 9 | Lead | [ ] |
