# Spec: Move the backend to .NET 11

Source: the user's description (log.md, step 1)
Status: Draft

## Problem

The backend `vandoxd`, its libraries and their tests are built for .NET 10 (`net10.0`, SDK 10.0.403, the `10.0` build image
and the `10.0-noble-chiseled` runtime image). The Product Manager has decided to move them to .NET 11 (GA 11.0.0, SDK
11.0.100). The 11 SDK and its analyzers report diagnostics the 10 SDK did not; since every diagnostic fails the build, the
solution does not build on .NET 11 until they are fixed. Several documents and a decision record
([0073](../../docs/decisions/0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md)) name .NET 10 as the backend's
platform. The Go agent is not affected.

## Scenarios

- S1: Given a checkout with the SDK of `global.json` installed, when a developer runs *Restore*, *Format*, *Build*, *Test*, the
  *Analyzer gate* and the *Coverage gate* of `.squad/stack.md`, then each passes on .NET 11, and the same tests run and pass as
  on .NET 10.
- S2: Given the image built from `deploy/backend/Dockerfile`, when the operator starts it with the compose file of
  [backend-host](../../docs/areas/backend-host.md), then `vandoxd` serves the web UI and the ingest endpoint as before, on
  the chiseled ASP.NET 11 runtime, as the non-root user, with the health check passing.
- S3: Given a signed-in user with a page open, when the session ends (sign-out, idle or absolute timeout, a password change),
  then the next event the page sends ends the live connection and is not processed, as
  [web-login](../../docs/areas/web-login.md) states today.
- S4: Given a page with a chart, when the chart is drawn or updated, then it looks and behaves as today
  ([web-ui](../../docs/areas/web-ui.md), *Charts*).
- S5: Given any `vandoxd` command (`-help`, `-version`, `hash-password`, `-healthcheck`, `templates`, `incidents`, …), when
  it runs to its end, then it prints the same text and exits with the same code as today
  ([backend-host](../../docs/areas/backend-host.md)).
- S6: Given a developer who writes code that one of the rules new with this change reports (S8949, CA2016, CA1830, CA2026,
  BL0013, BL0016), when they build, then the build fails on it.

## Errors and edge cases

- E1: The browser connection drops while a chart is drawn or updated: the drawing ends without an error of its own; the page
  is gone with the connection. A chart failure with the connection still open (a script error) is not hidden: it is reported
  as today.
- E2: A command is interrupted (Ctrl+C, container stop) while it writes its output: a command that reports an interruption
  today (`template rebuild interrupted`, `incidents` and `report` with exit code 1, …) still does; none ends with an unhandled
  exception instead.
- E3: The authentication state of a live connection changes while it is open: the next event is checked against the session
  of the current state, never of a stale one.

## Non-functional requirements

- N1: Compatibility: data directories, configuration files, the database schema, the wire format and the ingest API are
  unchanged; an existing installation upgrades by pulling the new image, with no migration step.
- N2: Support: .NET 11 is STS and ends support on 2028-11-14, the same day as .NET 10 (LTS); the upgrade shortens no support
  window. Patch releases come through Dependabot as today; the next major (12) stays excluded from automatic updates.
- N3: Security: the runtime image stays chiseled (no shell, no package manager), runs as `65532:65532`, and both base images
  stay pinned by digest ([0041](../../docs/decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md)).
  The web login guarantees of `.squad/project.md` (security area 3) hold unchanged.
- N4: Target host: the image is built for `linux/amd64` and must start on the reference host (Synology DS918+, Celeron J3455,
  Linux 4.4.302+, see `docs/BENCHMARKS.md`) as the `10.0-noble-chiseled` image does. No `aspnet:11.0-noble-chiseled` image
  exists; the chiseled ASP.NET 11 image is `11.0-resolute-chiseled` (Ubuntu 26.04). Whether it starts on that host is open
  (*Open questions*).
- N5: One .NET major per repository: `.squad/tools/dotnet_major.py` passes with every project, `global.json` and Dockerfile
  image on 11.

## Acceptance criteria

- AC1: Every C# project targets `net11.0`, `global.json` names SDK `11.0.100` with `rollForward` `latestFeature`, the
  `Microsoft.Data.Sqlite` and `Microsoft.Extensions.TimeProvider.Testing` packages are on 11.x, and *Build* and *Test*
  pass with every test of `main` still present and passing (S1, N1, N5).
- AC2: The *Analyzer gate* reports nothing for the whole solution on the 11 SDK with the newest `SonarAnalyzer.CSharp`
  release; `.editorconfig` sets `S8949` to `error` in its hand-maintained `[*.cs]` section, outside the
  `squad:sonar-rules` block; a rule new with this change that a deliberately offending line does not fail the build on
  is set to `error` there too (S6).
- AC3: The Dockerfile builds on the `sdk:11.0` image and runs on the chiseled `aspnet` 11 image, both pinned by a current
  digest, and the CI image build and compose smoke test pass (non-root user, no shell, health check, ingest port) (S2,
  N3, N5).
- AC4: The image starts and serves the web UI and the ingest endpoint on the reference host (S2, N4).
- AC5: A live connection whose session has ended processes no further event, also after its authentication state changed
  while it was open (S3, E3, N3).
- AC6: A chart whose drawing or update fails because the browser connection is gone ends without an exception; any other
  failure of a chart call reaches the caller as today (S4, E1).
- AC7: Every `vandoxd` command prints the same text and exits with the same code as on `main`; a command interrupted while
  it writes its output reports the interruption as it does today and ends without an unhandled exception (S5, E2).
- AC8: No document, comment or configuration states .NET 10 as the backend's current platform: a new decision record
  supersedes 0073, and `.squad/stack.md`, `.squad/project.md`, the project blocks of `AGENTS.md` and
  `.github/copilot-instructions.md`, `docs/ARCHITECTURE.md`, `docs/CONTRIBUTING.md`, `.github/dependabot.yml` and the
  Dockerfile name .NET 11 (N2).

## Out of scope

- The Go agent and its toolchain.
- New .NET 11 features or APIs beyond what a fixed diagnostic needs; behavior changes of any kind.
- Measurements recorded on .NET 10 (`docs/BENCHMARKS.md`, timings in area documents): they stay as measured; no
  re-measurement on .NET 11.
- Packages other than those named in AC1 and AC2 (Dependabot keeps them current).

## Open questions

- Q1: The runtime base. Not answered — escalated: `aspnet:11.0-noble-chiseled` does not exist on MCR (tag list read
  2026-11-11); the chiseled ASP.NET 11 images are `11.0-resolute-chiseled` (Ubuntu 26.04) and their variants, so the
  runtime base moves from Ubuntu 24.04 to 26.04. Whether that image starts on the reference host (Linux 4.4 kernel,
  Synology's Docker, a CPU without AVX) no one in the session can check; CI runs it only on a current kernel. AC4 waits
  for the Product Manager's answer on whether this base is accepted and when the image is tried on that host.
- Q2: Which new rules get an `.editorconfig` line. Answered: (Lead, trial build of `main` with only `net11.0` and
  SDK 11.0.100) CA1830, CA2016, CA2026, BL0013, BL0016 and S8949 are all reported at warning level, which
  `TreatWarningsAsErrors` turns into build errors, and `MSTEST0049` and `S8969` are already `error` in `.editorconfig`.
  A line for them would be redundant; S8949 gets one because the user asked for it explicitly, and AC2 keeps the check
  for a rule the newer Sonar release adds below warning level.
- Q3: Claims of the description checked against the trial build and the package feeds. Answered: confirmed are IDE0028 in 11 places of
  Vandox.Core; CA1830 in FtsQuery.cs and ImportRepository.cs; CA2016 and S8949 in the CLI commands and BackendApp.cs;
  BL0016 in ChartInterop.cs (three calls in the render path); BL0013 in SessionCircuitHandler.cs; CA2026 in tests
  (BackupRunTests.cs among them); `sdk:11.0` exists; `Microsoft.Data.Sqlite` and
  `Microsoft.Extensions.TimeProvider.Testing` 11.0.0 exist. Refuted: S8949 needs no Sonar update (10.35.0.4138 already
  reports it); S8969 is in 10.35.0.4138 too and is not reported by it on `main`. Not named in the description: CA1830
  also in `src/Vandox.Import/PathText.cs`; BL0016 in the test doubles `RecordingJsRuntime.cs`,
  `RecordingJsObjectReference.cs` and in `ChartInteropTests.cs`; S8949 and MSTEST0049 in `LockedTextWriterTests.cs`.
  The newest Sonar release is 10.36.0.15337; it may report more, which the *Analyzer gate* decides.
- Q4: Whether the project blocks of `AGENTS.md` may change in a product PR. Answered: yes (`AGENTS.md`, *Related skills*);
  `.github/copilot-instructions.md` must stay identical (`config-check.py`).

## Area documents

- [backend-host](../../docs/areas/backend-host.md), *Container image*: the runtime image is the chiseled ASP.NET 11 image
  (its distribution named if the document names one); nothing else changes.
- No other area document changes: the behavior of S3 to S5 is unchanged, and the fixes of E1 to E3 keep what
  [web-login](../../docs/areas/web-login.md), [web-ui](../../docs/areas/web-ui.md) and
  [backend-host](../../docs/areas/backend-host.md) state today.
