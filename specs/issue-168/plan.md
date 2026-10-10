# Plan: stack.md: non-interactive Format command and Debug allocation baselines

Source: Issue #168
Status: Draft
Tier: security — the change edits *Format*, a command the pipeline runs (Code Officer, step 7), and
`.squad/routing.md` (*Tiers*, the paragraph under the table) makes an edit to a command the pipeline runs a
`security` change even in `.squad/stack.md`; the rest of the change (a note on the Debug test build) alone
would be `docs`.

## Problem / root cause

`.squad/stack.md` line 39 defines *Format* as `gofmt -w . && reihitsu-format src tests tools`. The managed
Code Officer agent (`.claude/agents/squad-code-officer.md` line 25) runs *Format* "non-interactive", but
`reihitsu-format` 1.1.0 (pinned in `dotnet-tools.json`, also the installed global tool) prompts for
confirmation when a run covers more than 25 files, and a squad session has no terminal to answer it.
Separately, `.squad/stack.md` line 42 (*Test*) and line 44 (*Test with coverage*) run `dotnet test` without
`-c`, so the tests build Debug, while CI (`.github/workflows/ci.yml` lines 72/75 and 126/127) builds and tests
Release; nothing in `stack.md` or `docs/UNIT_TESTS.md` says so, and the allocation-bound pattern #17 needed is
not documented.

Claims of the issue, checked (scratch copy of `src tests tools` at `HEAD`, `DOTNET_ROOT=/usr/lib/dotnet`):

- *Format* is interactive — **confirmed**, more precisely: with stdin not a terminal,
  `reihitsu-format src tests tools` prints "This run would format 258 file(s), but standard input is not
  interactive, so the confirmation prompt cannot be answered. Use --force to format without confirmation." and
  exits with code 2, formatting nothing. The count is every file given (258 today; 247 at the time of #17), not
  the files that need formatting — `--check` reported "0 of 258 file(s) need formatting" on the same tree.
- `--force` formats only what needs it — **confirmed**: `--help` describes it as "Skip the confirmation
  prompt shown when more than 25 files would be formatted". On the clean tree, `--force` reported "Formatted 0
  of 258 file(s)", exit 0, no content change (`git status` clean) and no file modification time changed. With
  one file deliberately misformatted, it reported "Formatted 1 of 258 file(s)", only that file's modification
  time changed, and the file was restored byte-for-byte to the committed content (CRLF line endings kept,
  `git status` clean). `--check` never prompts.
- `DOTNET_ROOT` should be mentioned at *Format* — **partly refuted**: `stack.md` lines 48–49 already state that
  the global tool needs `DOTNET_ROOT` (verified: without it the global `reihitsu-format` aborts with "Download
  the .NET runtime"). The plan only ties that sentence to *Format* and *Format check* by name.
- *Test* runs without `-c Release`, so tests build Debug — **confirmed** (line 42; `Directory.Build.props`
  sets no configuration). CI runs Release — found on the way, not in the issue.
- `LogLineReader.ReadAsync` allocates about 104 bytes per call in Debug — **confirmed**: a scratch console
  program referencing `src/Vandox.Core` read 16 MiB of `\n` (16,777,216 lines) with `GC.GetTotalAllocatedBytes(true)`
  around a plain `ReadAsync` loop: Debug 1,744,837,640 bytes (104.0 per line), Release 104 bytes in the first
  round and 0 in the second. The issue's "33 KB in Release" over 64 MiB was not reproduced exactly (input size
  differs); the point — essentially nothing in Release — holds. Cause: in Debug the C# compiler emits every
  async state machine as a class, so each call allocates even when it completes synchronously.
- #17 solved it with a baseline loop — **confirmed**: `tests/Vandox.Core.Tests/MariaDbErrorLogParserTests.cs`
  lines 956–1029 bound `parseAllocated - readerAllocated`, with the baseline helper `ReaderAllocationAsync`
  (line 1115).

Related defect found on the way (template-managed, not fixed here): the template part of `CLAUDE.md` (line 90,
*Commands*, outside every `<!-- project:… -->` block) lists `reihitsu-format ./`, which prompts in the same way
and, from the repository root, covers more than `src tests tools`. That is a lesson for the template repository
(see *Out of scope / follow-ups*).

## Acceptance criteria

- [ ] AC1: The *Format* row of `.squad/stack.md` reads exactly
  `` `gofmt -w . && reihitsu-format src tests tools --force` (or `dotnet tool run reihitsu-format src tests tools --force`) ``;
  *Format check* is unchanged.
- [ ] AC2: The paragraph below the *Commands* table states that `DOTNET_ROOT` applies to *Format* and *Format
  check*, that without `--force` the formatter prompts when a run covers more than 25 files (counted over every
  file given), formats nothing and exits with code 2 in a non-interactive session, and that `--force` only skips
  the prompt and still rewrites just the files that need formatting.
- [ ] AC3: A paragraph below the *Commands* table states that *Test* and *Test with coverage* build the .NET
  tests Debug, that CI and the *Analyzer gate* build Release, and that a test must pass in both.
- [ ] AC4: *Writing tests* has a paragraph on .NET allocation-bound tests: measured with
  `GC.GetTotalAllocatedBytes(true)`, valid in Debug and Release; Debug allocates per async call (state machine as
  a class, `LogLineReader.ReadAsync` about 104 bytes per line, essentially nothing in Release); an absolute bound
  on code that calls such a method per line or record fails in Debug; measure beyond a baseline loop over the
  same input and bound the difference, naming `MariaDbErrorLogParserTests.ReaderAllocationAsync`.
- [ ] AC5: The diff changes `.squad/stack.md` only (besides `specs/issue-168/`); `python3 .squad/tools/config-check.py`,
  `python3 .squad/tools/scope-check.py --tier security` and *Format check* pass.

## Verification without tests

The change touches no production or test code: steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage
gate* of step 6 are not applicable (*Changes without production or test code* in `.squad/routing.md`).

| AC | Where | Who |
| -- | ----- | --- |
| AC1 | Read-only check of the diff against the exact text in *Approach*; live check: in step 7 the Code Officer runs the new *Format* (non-interactive) and reports exit code 0, "Formatted 0 of N file(s)" and no changed file in `git status`, then *Format check* exit 0 | Reviewer (step 8), Code Officer (step 7) |
| AC2 | Read-only check of the diff against *Approach*; the facts were verified in this plan (*Problem / root cause*) and can be re-run on a scratch copy | Reviewer (step 8), Security (diff review) |
| AC3 | Read-only check of the diff against *Approach* and `.github/workflows/ci.yml` lines 72–75 and 126–127 | Reviewer (step 8) |
| AC4 | Read-only check of the diff against *Approach* and `tests/Vandox.Core.Tests/MariaDbErrorLogParserTests.cs` lines 956–1029 and 1115 | Reviewer (step 8) |
| AC5 | `git diff --stat origin/main...HEAD`; `python3 .squad/tools/config-check.py`; `python3 .squad/tools/scope-check.py --tier security`; *Format check* | Orchestrator / Code Officer (step 7), Reviewer (step 8) |

## Approach

The Dev applies exactly these three edits to `.squad/stack.md` (the only file changed besides the working
records).

Edit 1 — line 39, the *Format* row, becomes:

```
| *Format* (Code Officer only in the squad) | `gofmt -w . && reihitsu-format src tests tools --force` (or `dotnet tool run reihitsu-format src tests tools --force`) |
```

Edit 2 — lines 48–49 (the `DOTNET_ROOT` paragraph) are replaced by these two paragraphs:

```
`reihitsu-format` needs `DOTNET_ROOT` when it runs as a global tool outside the SDK's directory (for example
`DOTNET_ROOT=/usr/lib/dotnet`), for *Format* and *Format check* alike. Without `--force` it asks for confirmation
when a run covers more than 25 files (it counts every file it is given, not only those that need formatting); in a
non-interactive session it cannot ask, so it formats nothing and exits with code 2. `--force` only skips that
prompt: the run still rewrites just the files that need formatting and leaves every other file untouched.

*Test* and *Test with coverage* build the .NET tests in the Debug configuration (no `-c`); CI
(`.github/workflows/ci.yml`) and the *Analyzer gate* build Release. A test must pass in both (*Writing tests* has
the consequence for allocation bounds).
```

Edit 3 — in *Writing tests*, a new paragraph directly after the paragraph that ends "no other suppression of
`S1215`." (line 156), separated by one blank line on each side:

```
Allocation-bound tests (.NET) measure with `GC.GetTotalAllocatedBytes(true)` and must hold in Debug, where *Test*
runs them, as well as in Release, where CI runs them. In Debug the compiler emits every async state machine as a
class, so each call of an async method allocates even when it completes synchronously: `LogLineReader.ReadAsync`
allocates about 104 bytes per line there and essentially nothing in Release. An absolute bound on code that calls
such a method per line or per record therefore fails in Debug for every implementation. Measure the allocation
beyond a baseline loop that reads the same input through that method alone, and bound the difference (as
`MariaDbErrorLogParserTests.ReaderAllocationAsync`).
```

Not chosen: switching *Test* to `-c Release` to match CI. It would change three commands the pipeline runs
(*Test*, *Test with coverage*, *Single test*), and a developer running `dotnet test` by hand would still build
Debug — so tests must hold in both configurations anyway. The issue proposes a note, and the note is what the
plan delivers.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| — | `.squad/stack.md` | Edits 1–3 above (Dev) |

## Signatures (for the Dev's skeleton)

None (no production or test code).

## Test files

None (steps 4 and 5 not applicable, see *Verification without tests*).

Existing test code that calls a changed signature: none.

## Areas

None: no change in product behavior.

## Documentation updates

- `.squad/stack.md` — Edits 1–3 (owner: Dev).
- `docs/CONTRIBUTING.md` line 33 keeps `reihitsu-format src tests tools`: it addresses a person at a terminal,
  who can answer the prompt. No other documentation changes.

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is touched: the change edits the toolchain
description only. *Format check* (the enforcing command, also in CI at `.github/workflows/ci.yml` line 67) is
unchanged, so nothing the squad or CI enforces is weakened.

## Security considerations

- [x] No limits on parsed input: no parser or input is touched.
- [x] No exception reaches a user: no code is touched.
- `--force` does not widen what the formatter writes (verified above: it rewrites only files that need
  formatting, keeps line endings). The global tool is installed unpinned by the SessionStart hook; a later
  version could change the flag's meaning. The `dotnet tool run` form uses the version pinned in
  `dotnet-tools.json` (1.1.0, verified), and *Format check* in CI still catches any formatting difference.

## Decision records

None: a flag that makes a documented command non-interactive and a test-writing note are routine; no choice
between real alternatives of lasting weight and no guarantee is touched (record 0074 already makes
`stack.md` the repository's own toolchain description).

## Out of scope / follow-ups

- Template lesson (orchestrator, step 12, template repository named in `.squad/template.json`): the template
  part of `CLAUDE.md` (*Commands*, profile `dotnet`) lists `reihitsu-format ./`, which prompts in a
  non-interactive session (more than 25 files, exit 2) and formats more than the stack's *Format*; suggested
  title "[Squad] CLAUDE.md dotnet Commands: non-interactive reihitsu-format (from LarsLaskowski/Vandox#168)".
