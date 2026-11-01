# Plan: Show the socket's UID and user in the new-listener findings

Source: Issue #304 | [spec.md](spec.md)
Status: Revised (1)
Tier: standard — a behavior change of the report's listener lines that routes one more external text through the existing, unchanged escaping (`MarkdownText.Code`, `ExternalText`) and adds a display cut; it creates, changes or removes no security control.
Security areas touched: 12 (display of external data: the socket's user name, a text a forged agent chooses, in the report, its download and the report page); 3 (Web UI login: the per-circuit memory figure of the report page in `.squad/project.md`, *Security areas*, is restated)

## Problem / root cause

Feature: see [spec.md](spec.md). The listener line is built in `ReportComposer.ListenerPieces` (`src/Vandox.Backend/Reports/ReportComposer.cs:885-892`) from `Proto`, `Local`, `Pid` and `Command` only.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Listener entries carry `Uid` (`uint?`), `User` and `MixedUids` | `src/Vandox.Core/Model/SocketOwner.cs` (`Uid`, `User`), `src/Vandox.Core/Model/Listener.cs` (`MixedUids`) | confirmed |
| 2 | The stored listener lists of both finding kinds are serialized `Listener` entries, so they keep the three fields; reading them decodes `List<Listener>` | `ListenerQueries.SerializeListeners` (`src/Vandox.Storage/ListenerQueries.cs:81-84`), used by `ListenerMarkWriter.cs:121,267`; `ListenerQueries.cs:265` deserializes | confirmed |
| 3 | The report line shows only PID and command, or `no owner` | `ReportComposer.cs:885-892`; `ReportText.cs:101-102,199-200` | confirmed |
| 4 | `WithCode` handles one code span per template; a template without `{0}` yields the prefix piece only | `ReportComposer.cs:346-370` | confirmed |
| 5 | The Markdown writer joins the pieces of a block, external ones through `MarkdownText.Code`; the page renders each external piece as an `ExternalText` in a `code` element, so a second code piece in one item needs no change in either | `MarkdownReportWriter.cs:114`; `Components/Reports/ReportContent.razor.cs:64-80` | confirmed |
| 6 | No other view lists listeners | `grep -rn "\.Command\b\|MixedUids\|\.Uid\b" src/Vandox.Backend`: only `ReportComposer.cs:891` and the incident view's process aggregate | confirmed |
| 7 | `Utf8Text.Cut(text, limit, out truncated)` cuts at a character boundary to at most `limit` UTF-8 bytes; it is `internal` to `Vandox.Core`, whose `InternalsVisibleTo` names only `Vandox.Core.Tests` and `Vandox.Import` | `src/Vandox.Core/LogParsing/Utf8Text.cs:43-71`; `src/Vandox.Core/Vandox.Core.csproj:17-18`; tests `tests/Vandox.Core.Tests/Utf8TextTests.cs` | confirmed |
| 8 | `ReportLimits` is the public home of the report's fixed limits | `src/Vandox.Core/Reports/ReportLimits.cs` | confirmed |
| 9 | Records 0115 and 0116 are unreleased | `git tag --contains` of the commits that added them prints nothing; the only tag is `v0.1.0` | confirmed |
| 10 | The golden reports are rendered from `ReportSamples.RichSummary` (`RichListeners`) and compared by `MarkdownReportWriterWritesTheGoldenReport` | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs:46-54`; `ReportSamples.cs:200-215` | confirmed |
| 11 | A text's page cost is set by its runs: `LogText.Build` merges consecutive plain or escaped characters into one `TextRun` with its own string, so the worst case alternates a plain and an escaped character | `src/Vandox.Backend/Logs/LogText.cs:63-110` | confirmed |
| 12 | Retained heap of a text with its runs (`LogText.Runs(text, LogText.MaxShortChars)`, 2,560 texts, `GC.GetTotalMemory(true)`): 64 × U+0001 1,097 B; 64 bytes alternating `a`/U+0001 4,848 B; 64 × `a` 454 B; 1,024 bytes alternating about 22.9 KiB | Security's scratch measurement in the plan review (`measure-Program.cs` in the session scratchpad) | confirmed |
| 13 | A name of 32 bytes alternating `a`/U+0001 (the worst case under the 32-byte cap) retains at most 3,072 B with its runs, so the names add at most about 7.9 MB per circuit (about 6.5 MB expected, half of fact 12's 64-byte figure) | to verify: the Tester's committed test `LogTextRunsOfAlternatingShortNameRetainBoundedHeap` (step 4) measures it with `HeapSampler.RetainedBytes` over 2,560 texts; a failure goes back to the Lead before the Dev writes the figure | unverified |

## Acceptance criteria

The feature's criteria are AC1 to AC10 of [spec.md](spec.md).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `MarkdownReportWriterWritesListenerWithOwnerAndSocketUid` (en, de); `ForensicsReporterRunWritesListenerSectionFromStore` adapted: the stored `telnetd` entry carries `uid` 0 and `user` `root`, and the expected line is ``  - tcp 0.0.0.0:23, PID 200 ` telnetd `, socket UID 0 ` root ` `` (store round trip) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`; `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs` |
| AC2 | `MarkdownReportWriterWritesListenerWithoutOwnerAndSocketUid` (en, de) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC3 | `MarkdownReportWriterWritesLowestOfSeveralUids`: with a user, without a user, and with a cut user (` (cut)` before ` (lowest of several)`) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC4 | `MarkdownReportWriterWritesSocketUidWithoutUser`: the line ends with `, socket UID 33` exactly (no backtick, no `—`) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC5 | `MarkdownReportWriterWritesSocketUidUnknownWithoutUid` (owner and no owner; never `socket UID 0`); `MarkdownReportWriterWritesSocketUidZeroAndLargest` (0 with user `root`, 4294967295 without user) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC6 | `MarkdownReportWriterEscapesSocketUserLikeCommand`, one row per user: a backtick run (`` a``b ``), `<b>x</b>`, U+202E (asserted to be in the input), U+0001, the six characters backslash `u202e`, three spaces, `1234`; the expected suffix is `", socket UID 5 " + MarkdownText.Code(user)`, and for the backtick, U+202E and spaces rows also a literal expected line, so an escaping fault in `MarkdownText` does not pass by comparing with itself | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC7 | `ReportContentShowsSocketUserAsText` (the user `<b>x</b> ``` ` + U+202E + line feed: in a `code` element, no element from it, the override a visible escape); `ReportContentShowsListenerSectionLikeTheMarkdown` gains the row `("en", "hostile")` with a section holding E1 and E8 users; `ReportDownloadEndpointWritesTheListenerSection` adapted: the stored entry carries `uid` 33 and a user of 40 characters with a backtick run inside the first 32 bytes, and the download holds the exact line | `tests/Vandox.Backend.Tests/ReportContentTests.cs`; `tests/Vandox.Backend.Tests/ReportDownloadEndpointTests.cs` |
| AC8 | the German rows of the AC1 to AC5 tests and of `MarkdownReportWriterCutsLongSocketUser` (` (gekürzt)`) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC9 | `MarkdownReportWriterWritesTheGoldenReport` (en, de) against the updated golden files; `MarkdownReportWriterWritesNewListenersBeforeOutages` with its expected arrays adapted (only the five listener lines change) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`; `testdata/report/forensics-en.md`, `testdata/report/forensics-de.md` |
| AC10 | `MarkdownReportWriterCutsLongSocketUser`, rows: 32 `a` (whole, no marker), 33 `a` (32 `a` and ` (cut)`), 31 `a` + `é` (2 bytes, crosses byte 32: 31 `a` and ` (cut)`), 30 `a` + an emoji of 4 bytes (30 `a`, cut), 1,024 × U+0001 (32 escapes `\u0001` then ` (cut)`), en and de; `LogTextRunsOfAlternatingShortNameRetainBoundedHeap` (the memory bound of the cut name, fact 13: 2,560 strings of 32 bytes alternating `a`/U+0001, each with `LogText.Runs(text, LogText.MaxShortChars)` kept alive, retain at most 3,072 B per text by `HeapSampler.RetainedBytes`, the same measurement with 32 × `a` as a sanity floor below it) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`; `tests/Vandox.Backend.Tests/LogTextTests.cs` |

## Approach

- **Line layout** (fixed by the spec; the item stays a level-1 list item under its finding): the existing pieces of `ListenerPieces`, then the UID part, then the optional markers, in this order:
  1. `Uid` is `null` → plain `ListenerUidUnknown` (`, socket UID unknown`); nothing else follows (`mixed_uids` needs a `uid`, wire rule).
  2. `Uid` set, `User` empty → `WithCode(ListenerUidNoUser, string.Empty, Number(uid))` (`, socket UID {1}`, no `{0}`, so no code piece).
  3. `Uid` set, `User` not empty → `shown = Utf8Text.Cut(User, ReportLimits.MaxShownUserBytes, out cut)`, then `WithCode(ListenerUid, shown, Number(uid))` (`, socket UID {1} {0}`), then plain `ListenerUserCut` when `cut`.
  4. `MixedUids` → plain `ListenerMixedUids` last.
- Texts (`ReportText`, English / German): `ListenerUid` `", socket UID {1} {0}"` / `", Socket-UID {1} {0}"`; `ListenerUidNoUser` `", socket UID {1}"` / `", Socket-UID {1}"`; `ListenerUidUnknown` `", socket UID unknown"` / `", Socket-UID unbekannt"`; `ListenerUserCut` `" (cut)"` / `" (gekürzt)"`; `ListenerMixedUids` `" (lowest of several)"` / `" (niedrigste von mehreren)"`.
- The cut is made in the composer, before any escaping, so the Markdown, the download and the page show the same prefix; the page's `ExternalText` cap of 2,048 displayed characters is never reached by a cut name (at most 192), and a cut name has at most 32 runs.
- `Utf8Text` becomes `public` with `Cut` public (`Decode` stays `internal`), so the backend reuses the tested cut rather than a second copy.
- Test samples: `ReportSamples.Entry` gains the optional parameters `uid`, `user`, `mixedUids`; `RichListeners` sets `telnetd` `uid` 0 `root` (S1), `tcp6 [::]:8443` `uid` 33 `www-data` (S2), `exim4` `uid` 0 `root` with `mixed_uids` (S3), `tcp 0.0.0.0:2121` `uid` 1000 without user (E2) and leaves `tftp`d` without `uid` (E3). The golden lines then end with `, socket UID 0 ` root ``, `, socket UID 33 ` www-data ``, `, socket UID 0 ` root ` (lowest of several)`, `, socket UID 1000` and `, socket UID unknown`.

## Options considered

1. **Where the name is cut.** In the composer (chosen): one place for all three outputs, the stored data untouched. In the storage reader (`ListenerQueries`): would also shrink the run's memory to about 6 MB, but a storage read would alter stored data for a display need and every other reader would get a cut name. Rejected; the run's 11 MB is held once per service.
2. **How the backend gets the cut.** `Utf8Text` made public (chosen). `InternalsVisibleTo Vandox.Backend` in `Vandox.Core.csproj`: a build-file change that opens every internal of Core to the backend. A copy of the loop in the backend: a second implementation of a tested rule. Both rejected.
3. **How the line gets its second code span.** Separate templates per UID form joined with the existing `WithCode` (chosen), so the code piece stays the only external piece of each template. A general multi-placeholder `WithCode`: changes every caller's helper for one use. Rejected.
4. **Cap of the shown name.** 32 bytes (chosen after the Security plan review; spec E8): the longest name `useradd` creates, at most 32 runs. 64 bytes (the first plan): Security measured 4,848 B a name in the worst case, about 12.4 MB per circuit (fact 12). Rejected. A cap on the runs of this field (a new `ExternalText` mode): a second rendering rule for one field, while the byte cap already bounds the runs. Rejected. Showing the name whole: up to 256 runs, the commands' cost again. Rejected.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `LogParsing/Utf8Text.cs` | class and `Cut` public |
| Vandox.Core | `Reports/ReportLimits.cs` | new constant `MaxShownUserBytes` |
| Vandox.Backend | `Reports/ReportComposer.cs` | `ListenerPieces` appends the UID part; new private `UidPieces` |
| Vandox.Backend | `Reports/ReportText.cs` | five new texts, English and German |
| tests | `Vandox.Backend.Tests/ReportSamples.cs`, `MarkdownReportWriterTests.cs`, `ForensicsReporterTests.cs`, `ReportContentTests.cs`, `ReportDownloadEndpointTests.cs`; `testdata/report/forensics-en.md`, `forensics-de.md` | tests and fixtures (Tester) |

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Core/LogParsing/Utf8Text.cs
public static class Utf8Text
public static string Cut(string text, int limit, out bool truncated)
internal static string Decode(ReadOnlySpan<byte> bytes, int limit, out bool truncated)   // unchanged

// src/Vandox.Core/Reports/ReportLimits.cs
/// <summary>The most UTF-8 bytes of a socket's user name the report shows.</summary>
public const int MaxShownUserBytes = 32;

// src/Vandox.Backend/Reports/ReportComposer.cs
private List<ReportPiece> ListenerPieces(Listener listener)   // unchanged signature, new body
private List<ReportPiece> UidPieces(Listener listener)

// src/Vandox.Backend/Reports/ReportText.cs (init properties like the others)
public string ListenerUid { get; init; } = string.Empty;
public string ListenerUidNoUser { get; init; } = string.Empty;
public string ListenerUidUnknown { get; init; } = string.Empty;
public string ListenerUserCut { get; init; } = string.Empty;
public string ListenerMixedUids { get; init; } = string.Empty;

// tests/Vandox.Backend.Tests/ReportSamples.cs (Tester)
internal static Listener Entry(string proto, string address, int port, int pid = 0, string command = "", uint? uid = null, string user = "", bool mixedUids = false)
```

The skeleton rewrites no existing file whole; `ReportComposer.cs`, `ReportText.cs`, `Utf8Text.cs` and `ReportLimits.cs` were read and get only the members above.

## Test files

`tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`, `tests/Vandox.Backend.Tests/ReportContentTests.cs`, `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs`, `tests/Vandox.Backend.Tests/ReportDownloadEndpointTests.cs`, `tests/Vandox.Backend.Tests/LogTextTests.cs`, with the fixtures `tests/Vandox.Backend.Tests/ReportSamples.cs` and `testdata/report/forensics-en.md`, `testdata/report/forensics-de.md`. The composer is private to the report and tested through the writer and the page, as today.

Existing test call sites: `ReportSamples.Entry` keeps its parameters and gains optional ones, so no call breaks. Tests whose input a new criterion handles differently, adapted by the Tester: `MarkdownReportWriterWritesTheGoldenReport` (golden files), `MarkdownReportWriterWritesNewListenersBeforeOutages` (the five listener lines of `RichListeners`), `ForensicsReporterRunWritesListenerSectionFromStore` (exact line), `MarkdownReportWriterWritesNoNewListenerVariants` (checks the `telnetd` line if it compares it exactly), `MarkdownReportWriterWritesRestOfNewListeners` (`MarkdownReportWriterTests.cs:779`: `"  - tcp 0.0.0.0:{port}, no owner, socket UID unknown"`, German `kein Besitzer, Socket-UID unbekannt`), `MarkdownReportWriterWritesCommandAndAgentIdAsCodeSpans` (lines 817 and 820: both listener lines gain `, socket UID unknown`) and `ReportContentShowsListenerCommandAsText` (`ReportContentTests.cs:220`: the expected piece gains the suffix, the count of 5 code elements stays). The Tester then runs the whole Backend test project once before handing to the Dev, to catch any other exact listener line. Kept unchanged: `ReportServiceTests` (checks the command piece only), `ReportDownloadEndpointWritesTheOutputOfTheReportCommand` (compares two outputs of the same writer), `Utf8TextTests` (visibility only), `ListenerComparerTests` and the storage tests (no change there).

Files no unit test can cover: none.

## Areas

- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md): the listener line forms, the creator's-UID sentence, the user name as external text and its 32-byte cut, the run's bound.
- [Web UI](../../docs/areas/web-ui.md): the user names as external text of the report page; the per-circuit bound.

## Documentation updates

All owned by the Dev, written once the code is green. Sweep done: `grep -rn "16 MB\|5.5 MB\|owner commands of the\|no owner" docs .squad/project.md README.md`.

- `docs/areas/detection-and-reports.md`:
  - *New listeners*, bullet *A new listener* ("each with protocol, local address and port and, when known, the owner's PID and command"): add "and the socket UID with its user".
  - *Content and format*, the listener bullet: the forms of the *Approach* in both languages, `(cut)` / `(gekürzt)` and `(lowest of several)` / `(niedrigste von mehreren)`, and one sentence that the socket UID is the UID of the socket's creator, not the user of the PID shown (a daemon that binds as root and runs as `www-data` reads `socket UID 0 root`), linking [Agent](../../docs/areas/agent.md) *Socket UIDs*.
  - *External text in the report*, first sentence: add "the user names of the listeners' sockets", and that they are shown cut to their first 32 UTF-8 bytes on a character boundary before they are escaped.
  - *Limits of the report*, the listener sentence (about 140 KB a finding, about 5.5 MB) and "plus the 5.5 MB of the listener section": each listener also holds its user name of up to 1,024 bytes, about 280 KB a finding, about 11 MB; the page figure in its parenthesis ("up to about 16 MB per circuit") becomes the measured one of the *Web UI* bullet below.
- `docs/areas/web-ui.md`: *Report page*, *External text* bullet: add "the user names of the new listeners' sockets"; *Limits and errors*, *Memory per circuit*: the section's sentence ("each with at most 64 listeners whose command … takes up to about 6 KiB with its runs, about 390 KB a finding … up to about 16 MB") is restated from the measurements: a text's cost is set by its runs, worst when plain and escaped characters alternate; a command of 1,024 bytes takes up to about 22.9 KiB with its runs (fact 12), about 1.5 MB a finding, **about 60 MB** for the commands; each user name, cut to 32 bytes, up to about 3 KiB (fact 13, the committed test's bound), about 7.9 MB; so **up to about 68 MB per circuit** for the section plus the render tree, which is not measured; and "the agent ID and each command" → "the agent ID, each command and each user name". The figure that predates #304 is named as corrected, and the follow-up issue (*Out of scope / follow-ups*) is linked.
- `.squad/project.md`, security area 3: "up to about 16 MB for the new listeners section" → "up to about 68 MB for the new listeners section (measured worst case, render tree aside)". Security area 12 (the sentence listing `Listener.Command`, `ListenerFinding.AgentId`, `ListenerTally.AgentId`): add `SocketOwner.User`, shown cut to `ReportLimits.MaxShownUserBytes` (32) UTF-8 bytes before it is escaped. A product PR may change `.squad/project.md` (`AGENTS.md`).
- `docs/ARCHITECTURE.md`: no hit; none.

## Architecture check

No guarantee of `.squad/project.md` is weakened: external text still reaches the report only as code spans and the page only as `ExternalText` (area 12); the page stays read-only and behind the login; the per-circuit bound stays fixed and is restated. No new flow, so `docs/ARCHITECTURE.md` is unchanged.

## Security considerations

- [ ] The 32-byte limit applies to the decoded .NET string, counted in UTF-8 bytes before escaping (the wire's 1,024-byte rule was checked at decode); a lone surrogate counts as U+FFFD (3 bytes) in `Utf8Text.Cut` and is escaped by `MarkdownText` as today.
- [ ] Exceptions reaching a user: none new; the composer only formats decoded data.

## Decision records

- [0116](../../docs/decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md) (Accepted, unreleased) — edited in place by the Lead: *Source* names #304, option 7 states the measured figures (the commands' share corrected) and why the name is cut to 32 bytes for the page.
- [0115](../../docs/decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md) (Accepted, unreleased) — edited in place: the follow-up sentence of *Consequences* now names #304.

## Out of scope / follow-ups

As in [spec.md](spec.md), *Out of scope*. Follow-up issue for the orchestrator to open:

- Title: `[Backend] Bound the report page's memory for the commands of the new listeners`. Body: "The report page keeps the new listeners section per circuit. A command of 1,024 bytes whose plain and escaped characters alternate takes about 22.9 KiB with its `LogText` runs (measured in the plan review of #304), so 40 findings of 64 listeners hold about 60 MB per circuit, not the 16 MB that `docs/areas/web-ui.md` stated before #304; the number of circuits is not bounded and the container has 512 MB. Bound this share (for example a cap on the runs or on the shown length of a listener's command on the page, or lower page caps of findings) and pin the per-text cost with a test, as #304 does for the socket's user name." Labels: `area: backend`.

## Challenge

- Minor (Devil's Advocate): the list of tests to adapt was incomplete (`MarkdownReportWriterWritesRestOfNewListeners`, `MarkdownReportWriterWritesCommandAndAgentIdAsCodeSpans`, `ReportContentShowsListenerCommandAsText`). Accepted: the three tests and the full run of the Backend test project are in *Test files*.

## Security review

- Finding 1 (blocking: the per-circuit bound rested on an estimate with the wrong worst case; the 64-byte cap adds about 12.4 MB). Accepted: the cap is 32 bytes (*Options considered* 4; spec E8, AC10), the figures are measured (facts 11, 12) or pinned by a committed test (fact 13, AC10 row), and the documents, record 0116 and `.squad/project.md` state them. The cap is a parameter of the option the Devil's Advocate already saw, not a new design.
- Finding 2 (non-blocking: the 16 MB base for the commands is understated, about 60 MB measured). Accepted: the sentences this change rewrites state the measured figure, and bounding that share is the follow-up issue in *Out of scope / follow-ups*; it predates #304, so it is not built here.
- Finding 3 (non-blocking: `.squad/project.md` area 12 lists the section's external texts field by field). Accepted: `SocketOwner.User` with its cut is in *Documentation updates*.
