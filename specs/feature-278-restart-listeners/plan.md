# Plan: Report listeners that appeared while the agent was stopped

Source: Issue #278 | [spec.md](spec.md)
Status: Revised (1)
Tier: standard — a behavior change across the agent (two wire fields and a new `listener_check` value), the storage write path (two
derived tables) and the forensics report (a new section) that creates, changes or removes no security control; it touches the parsing of
the wire format, the display of external text and the report page behind the login.
Security areas touched: 1 (Ingest authentication: `BatchWriter.Write` gains a step after the record insert, in the transaction that `records_agent_seq` and `ON CONFLICT DO NOTHING` guard; the derived rows are written only for a record whose insert returned an ID, and no records row is ever updated), 3 (Web UI login: the report page and its download read through a new gated reader, `GatedListenerHistoryReader`,
of the shared `WebReaderGate`, and the page's per-circuit document grows by the section), 10 (Parsing of external input: the wire format's
`connection_snapshot` gains `inventory`, `tables` and the `listener_check` value `start`, validated in `internal/model` and
`Vandox.Core.Model.ConnectionSnapshot`; the storage write path decodes a stored payload), 12 (Logging and display of external data: the
owner `command` of a listener and the agent ID are shown in the report, on the page and in the Markdown download)

## Problem / root cause

See [spec.md](spec.md), *Problem*: `compare` (`internal/connstats/inventory.go`) returns `none` for `prev == nil`, and `prev` lives in memory
only (`connstats.go:78`).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The agent's first run after a start reads `none`, like a skipped line, other tables or a cut privileged part | `internal/connstats/inventory.go`, `compare` | confirmed |
| 2 | The record's `listeners` list is a prefix of the inventory keys, in inventory order | `connstats.go:217-223` and `snapshot.go:486-493` both use `col.listeners.sorted()`; `fill` and `halve` keep a prefix (`snapshot.go:64-140`) | confirmed |
| 3 | A list cut for size is not told apart in the stored data from other incomplete aggregates | `fill`, `shorten` (`snapshot.go:51-133`) set only `aggregates_incomplete`, which has six other causes (`snapshot.go:469-471`) | confirmed |
| 4 | Wire format 1.1 is unreleased, so 1.1 fields may be added in place | `git ls-remote --tags origin`: `v0.1.0` only; `git merge-base --is-ancestor e2f5099 v0.1.0` fails (the 1.1 commit is not in the release); `internal/wire/wire.go:17` `MinorVersion = 1` | confirmed |
| 5 | No agent code emits the gap cause `agent_not_running` | `grep -rn "GapAgentNotRunning" internal cmd` outside tests: only `internal/model/gap.go:12,47` (constant and validation) | confirmed |
| 6 | `seq` comes from one persistent per-agent counter that increases strictly across restarts and reboots | `docs/areas/agent.md`, *Spool and sending*; `docs/areas/wire-format.md`, *Batch identity (0045)*; the spool that assigns it is #38, not built (contract; implementation #38) | confirmed |
| 7 | The store keeps (agent ID, `seq`) once for agent records, and a duplicate gets no typed row | `SchemaMigrator.cs:34` unique index `records_agent_seq`; `BatchWriter.Write` returns `false` when `ExecuteScalar` yields no id (`BatchWriter.cs:110-113`), before any typed insert | confirmed |
| 8 | A batch is written in one transaction on one connection, so a query of the write path sees the rows written earlier in the same batch | `BatchWriter` holds one `SqliteConnection` and `SqliteTransaction` for all its commands (`BatchWriter.cs:43-56`); measured by Security on .NET 10.0.402 with Microsoft.Data.Sqlite 10.0.12: a query on the same connection and transaction sees the uncommitted insert | confirmed |
| 9 | Agent snapshots are not stored by any product path today | `docs/areas/agent.md`, *Status* (records dropped until the spool exists); `PortRoutingMiddleware` answers 404 on the ingest listener | confirmed |
| 10 | The section fits between *Memory consumers* and the outages heading without a change of `IReportWriter` | `ReportComposer.Summary` (`ReportComposer.cs:51-69`) | confirmed |
| 11 | Each page of the report page is a new run of the whole report | record 0108, option 5; `ReportViewBuilder` keeps the summary of every run | confirmed |
| 12 | `IPEndPoint` equality matches the agent's key (`netip.Addr`, port): `0.0.0.0` and `::ffff:0.0.0.0` differ, equal address and port are equal | measured by Security (.NET 10.0.402): `IPEndPoint.Parse("0.0.0.0:23")` differs from `IPEndPoint.Parse("[::ffff:0.0.0.0]:23")` (hash codes too, a `HashSet` agrees), `[::]:8443` equals `[0:0::0]:8443` | confirmed |
| 13 | A finding's worst-case text: 64 listeners × a command of 1,024 UTF-8 bytes (up to 1,024 UTF-16 characters, 2 KiB) plus about 100 bytes of fields ≈ 140 KB; 20 + 20 findings ≈ 5.5 MB in the run. On the page each command is also an `ExternalText` with its runs (`ReportContent.razor.cs:78`, `LogText.Runs(piece.Text, LogText.MaxShortChars)`, up to 2,048 characters, 4 KiB), about 6 KiB each as `web-ui.md` counts every external text: 64 × 6 KiB ≈ 390 KB a finding, 40 findings ≈ 16 MB per circuit (corrected after review round 1; 5.5 MB counted the stored text only); the head of 64 agent IDs of at most 64 bytes is under 64 KB | arithmetic from `ModelLimits` short text (1,024 bytes), `LogText.MaxShortChars`, the caps of this plan and `docs/areas/web-ui.md` (per-circuit amounts) (arithmetic) | confirmed |
| 14 | The section adds at most 5 seconds to a report run over one year of snapshots (525,600 marks, 100 starts, 10,000 agent findings) | to verify: the Dev measures the three reads of the section (`ListenerTalliesAsync` and both `ListenerFindingsAsync`) on the development host over a database of that size written through `SqliteStore.WriteBatchAsync`, in step 6, and writes the figure into *Limits of the report*. Read so far: the section reads only the narrow derived tables (*Approach*) | unverified |
| 15 | The write path adds one index lookup per stored usable snapshot and, per batch, computes each affected start once with at most two payload decodes, however many records the batch holds | to verify: the Tester's decode-count test (AC14 row, step 5) and the Dev's `WriteThroughputTests` medians before and after plus one case of a start stored first and then a 7-day backlog of snapshots in chronological batches (step 6). Read so far: *Approach*, write path, deferred like `WriteTemplates` (`SqliteStore.cs:466`) | unverified |
| 17 | The wire allows 4,096 `new_listeners` in one snapshot; the agent writes at most 64 | `ConnectionSnapshot.cs:250` checks `ModelLimits.MaxItems` (4,096); `docs/areas/wire-format.md:181`; agent `MaxNewListeners = 64` (`connstats.go:31`) | confirmed |
| 18 | `IPEndPoint.ToString()` writes an IPv4-mapped address in .NET's form (`[::ffff:0:0]:23` for the producer's `[::ffff:0.0.0.0]:23`), the same form the backend's JSON writer stores | measured by Security; `IpEndPointJsonConverter.Write` writes `value.ToString()` (`IpEndPointJsonConverter.cs:30`) | confirmed |

## Acceptance criteria

The criteria are those of [spec.md](spec.md), AC1 to AC14.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestCompare_FirstRunReportsStart` (the existing `TestCompare_BaselineReportsNone`, renamed and changed to `start`; a later run without a skipped line still compares); `SqliteStoreListenerFindingsComparesStartWithPreviousBySeq` (rows: base and start of one agent; two stored start snapshots back to back: the second compared with the first, two starts in the tally; E2 and E3: a base, then one start after a `seq` gap without a snapshot: compared with the base, one start in the tally; two agents with interleaved `seq` values compared only within each agent) | `internal/connstats/inventory_test.go`; `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC2 | `TestInventoryState` (rows: no cut → `all`); `ListenerComparerCompareWholeInventoriesComparesAllPorts` (S1 `tcp 0.0.0.0:23` and S2 `tcp6 [::]:8443` both new, outcome all ports) | `internal/connstats/inventory_test.go`; `tests/Vandox.Core.Tests/ListenerComparerTests.cs` |
| AC3 | `TestInventoryState` (rows: inventory cut at 4,096 with unprivileged keys kept → `privileged`; a size cut of the record after the privileged part → `privileged`); `TestConnections_SizeCutOfListenersSetsInventory`; `ListenerComparerCompareCutInventoryComparesPrivilegedOnly` (either side `privileged`: new `0.0.0.0:25` listed, new `0.0.0.0:8443` not) | `internal/connstats/inventory_test.go`; `internal/connstats/connstats_test.go`; `tests/Vandox.Core.Tests/ListenerComparerTests.cs` |
| AC4 | `TestInventoryState` (rows: a skipped line → `skipped`, also with a cut; cut inside the privileged part of the inventory → `cut`; a size cut inside the privileged part → `cut`); `TestConnections_SnapshotCarriesInventoryAndTables` (all four tables, and without `tcp6`/`udp6`); `ListenerComparerCompareWithoutBaseIsNoEarlierInventory`; `ListenerComparerCompareSkippedOrOtherTablesIsNotComparable` (rows: `skipped` before, `skipped` after, tables differ); `ListenerComparerCompareCutPrivilegedPartIsPrivilegedIncomplete` (rows: `cut` before, `cut` after; `skipped` wins over `cut`); `MarkdownReportWriterWritesNotComparedReasons` (three reasons × `en`/`de`, exact texts of AC11) | `internal/connstats/inventory_test.go`; `internal/connstats/connstats_test.go`; `tests/Vandox.Core.Tests/ListenerComparerTests.cs`; `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC5 | `ListenerComparerCompareOwnerChangeIsNotNew`; `ListenerComparerCompareEphemeralUdpNotReported` (rows: `udp` and `udp6` on 32767 listed, 32768 and 60999 not, 61000 listed; `tcp` on 40000 listed); `ListenerComparerCompareMappedAddressIsAnotherListener` (`[::ffff:0.0.0.0]:23` after `0.0.0.0:23` is new); `SqliteStoreListenerFindingsComparedWithoutNewListenerIsNoFinding` (counted in the tally, not listed) | `tests/Vandox.Core.Tests/ListenerComparerTests.cs`; `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC6 | `ListenerComparerCompareListsAtMost64InInventoryOrder` (70 new: the first 64 in inventory order, omitted 6, owner PID and command from the later snapshot, none when the entry has no PID); `MarkdownReportWriterWritesRestOfNewListeners` (`and 6 more` / `und 6 weitere`); `MarkdownReportWriterWritesCommandAndAgentIdAsCodeSpans` also asserts the agent ID as a code span in all three finding texts and the head | `tests/Vandox.Core.Tests/ListenerComparerTests.cs`; `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC7 | `SqliteStoreListenerFindingsKeepsAgentFindings` (a snapshot with `listener_check` `privileged`, two `new_listeners` and `new_listeners_omitted` 3: one agent finding with both listeners, omitted 3, outcome ports 1 to 1023 only; a snapshot with `all` and none: no finding; a snapshot with 4,096 `new_listeners` and omitted 5: the finding holds 64, omitted 4,037) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC8 | `SqliteStoreListenerFindingsSameForAnyStoreOrder` (rows: base and start in one batch; the start in one batch, its base in a later one; an older base, then the start, then a base between them in a later batch, which replaces the comparison; the same batch written twice; each row ends with the same tally and findings); `SqliteStoreListenerFindingsSameAtTheCapForEitherOrder` (two agents' findings with the same capture time at the cap of 20, stored in both orders: the same page); `SqliteStoreListenerWriteUndecodableBaseIsNotComparable` (a stored base whose `data` the test overwrites with a value that does not decode, then its start: outcome not comparable, the batch stored); `SqliteStoreListenerWriteMaximalSnapshotRoundTrips` (a snapshot at the wire's limits stored and compared: decoded without error); `SqliteStoreListenerFindingsOrderBySeqWhenTheClockStepsBack` (start captured before its base: still compared with it; findings of one agent returned in `seq` order by the composer) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC9 | `SqliteStoreListenerFindingsIgnoresOtherSnapshots` (rows: origin `import` source `lsof`; origin `agent` source `connections` without `listener_check`; without `inventory`; without `tables`; another source: no mark, no comparison, not in the tally); `TestConnectionSnapshot_Validate` (new rows: `start`, each `inventory` value, `tables` with a bad protocol and a duplicate); `ConnectionSnapshotValidateAcceptsTheNewFields` and `ConnectionSnapshotValidateRefusesBrokenFieldRules` (the same rows in C#) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`; `internal/model/connection_test.go`; `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs` |
| AC10 | `MarkdownReportWriterWritesCommandAndAgentIdAsCodeSpans` (a command with `<b>x</b>`, a run of three backticks, U+202E and a line feed: written through `MarkdownText.Code`, the escapes `\u202e` and `\n` present in the output; the test asserts that its expected text still holds the backslash, *Escapes in files*); `ReportContentShowsListenerCommandAsText` (the page: `<code>` with the visible escape, no element from the command) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`; `tests/Vandox.Backend.Tests/ReportContentTests.cs` |
| AC11 | `MarkdownReportWriterWritesNewListenersBeforeOutages` (rows `en`, `de`: heading, head line, a start finding and an agent finding with the phrases of the table, position after *Memory consumers* and before *Outages*); `MarkdownReportWriterWritesNoNewListenerVariants` (rows: no agent snapshot → no-data text; starts all compared and no finding → `No new listener found.`; a start not compared → its finding and `No new listener found among the compared inventories.`; both languages); `ForensicsReporterRunWritesListenerSectionFromStore` (S1 end to end on a real store); `ReportContentShowsListenerSectionLikeTheMarkdown` (page pieces equal the Markdown pieces); the golden reports `testdata/report/forensics-{en,de}.md` regenerated with the section | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`; `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs`; `tests/Vandox.Backend.Tests/ReportContentTests.cs` |
| AC12 | `SqliteStoreListenerTalliesListsAtMost64Agents` (65 agents: the 64 lowest IDs, 1 omitted); `MarkdownReportWriterNotesListenerCaps` (the three notes in both languages) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`; `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC13 | `SqliteStoreListenerFindingsCapsEachKindSeparately` (1,000 agent findings and one start finding on `tcp 0.0.0.0:23` older than all of them: the start page holds it, total 1; the agent page holds the latest 20, total 1,000) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC14 | `SqliteStoreListenerFindingsReadsNoPayload` (after the writes the test overwrites `records.data` of every stored snapshot with `{}` by SQL; tallies and both finding pages are unchanged); `ListenerMarkWriterDecodesEachStartOncePerBatch` (a stored start, then backfill batches of 10 and of 1,000 snapshots below it: `PayloadDecodes` is 2 per batch for both sizes); the time bound by the Dev's measurement of *Verified facts* row 14 | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`; `tests/Vandox.Storage.Tests/ListenerMarkWriterTests.cs` |

## Approach

**Agent (Go).** `compare` returns `model.ListenerCheckStart` when `prev == nil` (all other `none` cases stay). Every snapshot carries
`Tables` (the protocols of the tables read, in table order, from `col.read`) and `Inventory`, the state of the record's own `listeners`
list, computed by `inventoryState` from the run's inventory and the length of the list as it finally stands: `skipped` when a table line
was skipped; `cut` when the inventory was cut inside its privileged part or the list holds fewer entries than the inventory has on ports
below 1024; `all` when the inventory was not cut and the list holds every key; `privileged` otherwise. `fitRecord` sets it before every size
check, because `shorten` can halve the list. Nothing else changes: the agent's own comparison, full snapshots and caps stay.

**Wire and model (Go and C#).** `start`, `inventory` (four values) and `tables` (distinct protocols) are optional and omitted when empty;
`new_listeners` still needs `all` or `privileged`. Field errors use the model's one-of and protocol texts, plus `duplicate protocol` for
`data.tables[<i>]`. The golden batch gains both fields and a `start` snapshot (record 0075).

**Comparison (C#, `ListenerComparer`).** A pure function of the earlier and the later snapshot with the agent's rules, in the order of spec
AC4 (no earlier; `skipped` or other tables; `cut`), then all ports or ports below 1024; keys (`proto`, `local`); spec AC5 and AC6.

**Storage (C#).** Schema step 11 adds `connection_marks` (one row per usable agent snapshot: `record_id`, `agent_id`, `seq`, `captured_at`,
`listener_check`, `new_count`, `new_listeners` JSON for an agent finding) with a unique index on (`agent_id`, `seq`) and a partial index for
findings on (`captured_at`, `agent_id`, `seq`), and `listener_comparisons` (one row per start: the start's `record_id`, `agent_id`, `seq`, `captured_at`, the base's `record_id`,
`seq` and `captured_at` or `NULL`, `outcome`, `new_count`, `new_listeners`, `finding`) with a partial index for findings on (`captured_at`, `agent_id`, `seq`). A snapshot is
**usable** when its origin is `agent`, its source `connections`, it has an agent ID, `listener_check`, `inventory` and at least one table.
`BatchWriter.Write` hands a stored usable snapshot to `ListenerMarkWriter.Write` in the same transaction, only after the record insert
returned an ID (a duplicate writes nothing, fact 7): it inserts the mark, storing an agent finding's `new_listeners` cut to the first 64
with the rest added to the omitted count (a `long`: the agent's `new_listeners_omitted` plus the entries cut), and notes the **affected
starts**: the mark itself when it is a start, and the agent's next mark by `seq` when that is a start. `BatchWriter.WriteListenerComparisons`
(called by `SqliteStore.WriteBatchAsync` next to `WriteTemplates`, before the commit) computes each affected start once: it reads the
start's predecessor mark by `seq`, decodes the two payloads and inserts or replaces the start's `listener_comparisons` row. So a batch decodes
at most two payloads per affected start, and a backfill of many snapshots below one stored start decodes two in all. A stored payload that
does not decode makes that start `not comparable` (no error, the batch is kept). The only rows the write path ever updates are
`listener_comparisons` rows; no `records` row is updated. The reads of the report: `ListenerTalliesAsync` (per
agent ID, the 64 lowest: marks, first and last capture time, starts per outcome; and the number of further agent IDs) and
`ListenerFindingsAsync` (of one kind, the latest 1 to 20 ordered by capture time descending, then agent ID, then `seq` descending, so arrival order never matters, and the total), reading only the derived tables. Each read runs its count and row queries on one
reader lease (`SqliteStore.ReadAsync`), without a shared transaction, as the other multi-query reads do: a batch committed between
two of its queries can make a total differ from the rows by that batch. Only the write path runs in a transaction.

**Report (C#).** `ForensicsReporter` reads the tallies and the two finding pages after the incidents' first pass and sets
`ForensicsSummary.Listeners`; `ReportComposer.Summary` adds the section after the consumers, findings sorted by agent ID, then `seq`;
`Notes` adds the cap notes. The texts are in `ReportText` (both languages, *Texts* below). The agent ID and the command are
`ReportPiece.Code`; protocol, endpoint (`IPEndPoint.ToString()`, the backend's numeric form, fact 18), PID, times and counts are plain. The three finding
texts hold the agent ID inside the sentence: the composer splits each template at `{1}`, formats both parts with the other arguments and
puts `ReportPiece.Code(agentId)` between them (the head line likewise at `{0}`).
`ReportService`, `ReportCommand` and `ServeCommand` pass the new reader; the web goes through `GatedListenerHistoryReader` (one slot of the
shared `WebReaderGate` per call, as `GatedIncidentStore`).

**Texts** beyond the phrases of spec AC11, which `ReportText` holds as `NewListenersHeading`, `ListenerAllPorts`, `ListenerPrivilegedOnly`,
`ListenerNoEarlier`, `ListenerPrivilegedIncomplete`, `ListenerNotComparable`, `ListenerRest`, `NoNewListener`, `NoNewListenerCompared`,
`NoAgentSnapshots` (`{0}`… are plain pieces; the agent ID and the command are code spans):

| Key | English | German |
| --- | ------- | ------ |
| `ListenerAgentHead` | `Agent {0}: {1} connection snapshots from {2} to {3}; {4} agent starts: {5} compared on all ports, {6} on ports 1 to 1023 only, {7} not compared` | `Agent {0}: {1} Verbindungs-Snapshots von {2} bis {3}; {4} Agentenstarts: {5} auf allen Ports verglichen, {6} nur auf Ports 1 bis 1023, {7} nicht verglichen` |
| `ListenerStartFinding` | `{0}, agent {1}, after an agent start (compared with {2}), {3}: {4} new` | `{0}, Agent {1}, nach einem Agentenstart (verglichen mit {2}), {3}: {4} neu` |
| `ListenerStartNotCompared` | `{0}, agent {1}, after an agent start, not compared: {2}` | `{0}, Agent {1}, nach einem Agentenstart, nicht verglichen: {2}` |
| `ListenerAgentFinding` | `{0}, agent {1}, found by the agent, {2}: {3} new` | `{0}, Agent {1}, vom Agenten gefunden, {2}: {3} neu` |
| `ListenerEntry` / `ListenerEntryNoOwner` | `{0} {1}, PID {2} {3}` / `{0} {1}, no owner` | `{0} {1}, PID {2} {3}` / `{0} {1}, kein Besitzer` |
| `CapStartFindingsNote` / `CapAgentFindingsNote` / `CapAgentsNote` | `Only the latest {0} findings after an agent start are listed; {1} earlier ones are left out.` / `Only the latest {0} findings of the agent are listed; {1} earlier ones are left out.` / `Only the first {0} agent IDs are listed; {1} more are left out.` | `Nur die letzten {0} Funde nach einem Agentenstart sind aufgeführt; {1} frühere fehlen.` / `Nur die letzten {0} Funde des Agenten sind aufgeführt; {1} frühere fehlen.` / `Nur die ersten {0} Agenten-IDs sind aufgeführt; {1} weitere fehlen.` |

A start counts as compared on all ports or on ports 1 to 1023 by its outcome; `{4}` of a start finding and `{3}` of an agent finding is
the listed count plus the omitted one. `No new listener found.` is written when no finding holds a new listener and no start is
uncompared; `…among the compared inventories.` when none holds a new listener but a start was not compared.

## Options considered

Record 0116 lists eight options with their reasons (start detection: stored-data inference, `agent_not_running`, an inventory file, new
fields (chosen); comparison at report time or when stored (chosen); one cap or one per kind (chosen); capture time or `seq` (chosen)). One
more: a SQLite expression index on `json_extract(data, …)` instead of the `connection_marks` table — no write-path code, but a
query must repeat the exact expression to use the index, a covering read of expressions is not certain on the SQLite version bundled with
Microsoft.Data.Sqlite (not measured), and the comparison still needs a stored result; rejected for a typed table like `metrics` and
`log_lines`.

## Affected projects and types

The files per task are in [tasks.md](tasks.md); new and changed members in *Signatures*. Rewritten in place: `internal/connstats/inventory.go`
(`compare`), `snapshot.go` (`fitRecord`), `connstats.go` (`Collect`), `BatchWriter.Write`, `SchemaMigrator` (step 11),
`ForensicsReporter.RunAsync`, `ReportComposer.Summary` and `Notes`, `ReportText` (both instances). Test data regenerated by the Tester:
`testdata/wire/all-kinds.jsonl`, `testdata/report/forensics-{en,de}.md`.

## Signatures (for the Dev's skeleton)

Go:

```go
// internal/model/connection.go
const ListenerCheckStart ListenerCheck = "start"
type InventoryState string
const (
	InventoryAll        InventoryState = "all"
	InventoryPrivileged InventoryState = "privileged"
	InventoryCut        InventoryState = "cut"
	InventorySkipped    InventoryState = "skipped"
)
// ConnectionSnapshot gains, after NewListenersOmitted:
	Inventory InventoryState `json:"inventory,omitempty"`
	Tables    []Proto        `json:"tables,omitempty"`

// internal/connstats/inventory.go
func inventoryState(inv *inventory, listed int) model.InventoryState
// internal/connstats/connstats.go
func tableList(mask uint8) []model.Proto
// internal/connstats/snapshot.go (changed)
func fitRecord(start time.Time, snap *model.ConnectionSnapshot, inv *inventory) (model.Record, error)
```

C#:

```csharp
// Vandox.Core.Model.ConnectionSnapshot (new properties, JSON names inventory and tables, omitted when null)
public string? Inventory { get; set; }          // empty reads as null, like ListenerCheck
public List<string>? Tables { get; set; }

// Vandox.Core.Reports
public enum ListenerOutcome { NoEarlierInventory, NotComparable, PrivilegedIncomplete, AllPorts, PrivilegedOnly }
public enum ListenerFindingKind { AgentStart, Agent }
public sealed class ListenerComparison
{
    public ListenerOutcome Outcome { get; init; }
    public IReadOnlyList<Listener> NewListeners { get; init; } = [];
    public long Omitted { get; init; }
}
public static class ListenerComparer
{
    public const int MaxNewListeners = 64;
    public const int PrivilegedPorts = 1024;
    public static ListenerComparison Compare(ConnectionSnapshot? earlier, ConnectionSnapshot later);
}
public sealed class ListenerFinding
{
    public ListenerFindingKind Kind { get; init; }
    public string AgentId { get; init; } = string.Empty;
    public ulong Seq { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? BaseCapturedAt { get; init; }
    public ListenerOutcome Outcome { get; init; }
    public IReadOnlyList<Listener> Listeners { get; init; } = [];
    public long Omitted { get; init; }
}
public sealed class ListenerTally
{
    public string AgentId { get; init; } = string.Empty;
    public long Snapshots { get; init; }
    public DateTimeOffset FirstAt { get; init; }
    public DateTimeOffset LastAt { get; init; }
    public long Starts { get; init; }
    public long ComparedAll { get; init; }
    public long ComparedPrivileged { get; init; }
    public long NotCompared { get; init; }
}
public sealed class ListenerReport
{
    public const int MaxAgents = 64;
    public const int MaxFindings = 20;
    public IReadOnlyList<ListenerTally> Agents { get; init; } = [];
    public long AgentsOmitted { get; init; }
    public IReadOnlyList<ListenerFinding> StartFindings { get; init; } = [];
    public long StartFindingsTotal { get; init; }
    public IReadOnlyList<ListenerFinding> AgentFindings { get; init; } = [];
    public long AgentFindingsTotal { get; init; }
}
// ForensicsSummary gains
public ListenerReport Listeners { get; set; } = new();

// Vandox.Storage
public interface IListenerHistoryReader
{
    Task<ListenerTallyPage> ListenerTalliesAsync(CancellationToken cancellationToken);
    Task<ListenerFindingPage> ListenerFindingsAsync(ListenerFindingQuery query, CancellationToken cancellationToken);
}
public sealed class ListenerTallyPage
{
    public IReadOnlyList<ListenerTally> Agents { get; init; } = [];   // the ListenerReport.MaxAgents lowest agent IDs
    public long Omitted { get; init; }                               // further agent IDs
}
public sealed class ListenerFindingPage
{
    public IReadOnlyList<ListenerFinding> Findings { get; init; } = [];  // the latest Limit, by capture time, then record ID, descending
    public long Total { get; init; }
}
public sealed class ListenerFindingQuery
{
    public ListenerFindingKind Kind { get; set; }
    public int Limit { get; set; }                  // 1 to ListenerReport.MaxFindings, else "store: invalid query: limit must be between 1 and 20"
}
internal sealed class ListenerMarkWriter : IDisposable
{
    internal ListenerMarkWriter(SqliteConnection connection, SqliteTransaction transaction);
    internal static bool IsUsable(DataRecord record, string agentId);
    internal void Write(long recordId, string agentId, DataRecord record, ConnectionSnapshot snapshot);
    internal void WriteComparisons(CancellationToken cancellationToken);   // each affected start once
    internal long PayloadDecodes { get; }                                 // for the bound test
}
// BatchWriter gains
internal void WriteListenerComparisons(CancellationToken cancellationToken);
internal static class ListenerQueries   // the SQL texts of ListenerMarkWriter and the two reads, and the row mapping
// SqliteStore : … , IListenerHistoryReader

// Vandox.Backend.Reports
public sealed class GatedListenerHistoryReader : IListenerHistoryReader
{
    public GatedListenerHistoryReader(IListenerHistoryReader inner, WebReaderGate gate);
}
// changed constructors (one parameter added after incidents)
public ForensicsReporter(IRecordReader records, IIncidentStore incidents, IListenerHistoryReader listeners, SignatureCatalog catalog, TimeSpan lookback, TimeProvider clock)
public ReportService(IRecordReader records, IIncidentStore incidents, IListenerHistoryReader listeners, SignatureCatalog catalog, TimeSpan lookback, TimeProvider clock)
// ReportText gains the string properties of the *Texts* table (init, both instances filled)
```

Store errors: an undecodable `new_listeners` value is `store: reading listener findings: decoding listeners`. In the write path an
undecodable stored payload is not an error: that start's outcome is `not comparable`.

## Test files

- `internal/connstats/inventory_test.go`, `internal/connstats/connstats_test.go`, `internal/model/connection_test.go`,
  `internal/wire/golden_test.go` (fixture regenerated with `VANDOX_UPDATE_GOLDEN=1`)
- `tests/Vandox.Core.Tests/ListenerComparerTests.cs` (new), `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`,
  `tests/Vandox.Core.Tests/WireContractTests.cs` (the golden batch's new values)
- `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` (new), `tests/Vandox.Storage.Tests/ListenerMarkWriterTests.cs` (new), `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` (schema version
  11)
- `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`, `ReportContentTests.cs`, `ForensicsReporterTests.cs`,
  `GatedListenerHistoryReaderTests.cs` (new, coverage pass: one slot per call, released on failure)

Existing test code that calls a changed signature, adapted by the Tester: `TestCompare_BaselineReportsNone` and the `nil`-previous calls
of `checkCompare` (`inventory_test.go:79,82`), `wantFlags(…, model.ListenerCheckNone)` of a first run (`connstats_test.go:288,539,660`, and `:705` and `:1399` in
`TestConnections_SkippedLineMakesAggregatesIncomplete`, which also assert `inventory` `skipped`; each checked: first run → `start`); every `fitRecord` call in tests; `new ForensicsReporter(…)` in `ForensicsReporterTests.cs:372`; `new
ReportService(…)` in `ReportServiceTests.cs:154`, `ReportPageHarness.cs:61` and `ReportDownloadEndpointTests.cs` (its `Service` helper);
`ReportSamples` (sets `Listeners` for the rich sample); the golden files `testdata/report/forensics-{en,de}.md`; a schema-version assertion
in `SqliteStoreOpenTests`. A test double for the new reader where a test uses `ScriptedRecordReader`: `ScriptedListenerHistoryReader`
(scripted tallies and pages, copies of every query with its token), `tests/Vandox.Backend.Tests`.

Files no unit test can cover: none.

## Areas

As spec *Area documents*, plus [Storage](../../docs/areas/storage.md): *Schema* (version 11, both tables, their indexes), *Writing* (the
derived rows, the cap of 64, the deferred computation, `not comparable` for an undecodable payload), *Reading* (the two reads, their order
and limits), *Retention (planned)* (the derived rows go with their records).

## Documentation updates

Sweep hits: `wire-format.md:179,189` (`listener_check` values), `storage.md:36-63` (version, tables, indexes),
`detection-and-reports.md:407,415-424,585` and `ARCHITECTURE.md:171-172` (what the report reads; `web-ui.md:273` names only log lines and
stays), `agent.md:465` (`#278`), `.squad/project.md` (areas 1, 3, 10, 12, test doubles).

| Document | Update | Owner |
| -------- | ------ | ----- |
| `docs/areas/agent.md` | as *Areas* | Dev |
| `docs/areas/wire-format.md` | as *Areas* | Dev |
| `docs/areas/storage.md` | as *Areas* | Dev |
| `docs/areas/detection-and-reports.md` | as *Areas*, with the measured time of fact 14 | Dev |
| `docs/areas/web-ui.md` | as *Areas* | Dev |
| `docs/ARCHITECTURE.md` | *Data flow*, the forensics report paragraph: it also reads the stored listener comparisons; link 0116 | Dev |
| `docs/BENCHMARKS.md` | the section's measurement (fact 14) | Dev |
| `.squad/project.md` | area 1 (`BatchWriter` writes the derived listener rows only after an inserted record and updates only `listener_comparisons`), area 3 (the report reads through `GatedListenerHistoryReader` too; the page's per-circuit document grows by at most about 16 MB, fact 13), area 10 (the new fields), area 12 (command and agent ID of the section only through `MarkdownText.Code` and `ExternalText`) | Dev |
| `.squad/project.md` | *Test doubles*, database row: `ScriptedListenerHistoryReader` | Tester |
| `docs/decisions/0116-…md`, index, 0115 residual | written | Lead (done) |

## Architecture check

The three guarantees of `.squad/project.md` hold: an uncompared start says why and an unknown never reads as "no new listener" (no gaps);
the collector does no new I/O (never blocks); no alert is added (backfill). The agent stays outbound-only, unprivileged and without a new
file; the backend gets no new listener or route. `ARCHITECTURE.md` links 0116 (Dev).

## Security considerations

- [x] Limits on parsed input: `tables` holds at most four distinct protocols (the distinct rule, checked after decoding, like every list
  limit); `inventory` is an enumeration; the stored `new_listeners` JSON is cut to 64 entries by `ListenerMarkWriter` (the wire allows 4,096, fact 17),
  the rest counted, and read back through the payload options; a finding therefore holds at most 64 listeners whatever the agent sends.
- [x] Exceptions that reach a user: a store failure of the section's reads is a `StoreException` and shows the page's existing failure text
  (`The report could …`, web UI area) and exit 1 of `vandoxd report`; the log holds the exception only. An undecodable stored payload in the
  write path makes that start `not comparable` instead of failing the batch, so a resent batch is never refused for it.
- External text: the command and the agent ID reach the Markdown only through `MarkdownText.Code` and the page only through
  `ReportPiece.Code` (`<code><ExternalText …/></code>`); never in a heading or table cell.
- Forged data: a compromised agent can forge snapshots and so findings (as it can forge any record); bounded by the caps.

## Decision records

- `docs/decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md` (Proposed)
- `docs/decisions/0115-…` (Accepted, unreleased): the residual of #278 now points to 0116 (edited in place)

## Out of scope / follow-ups

- A rebuild of `listener_comparisons` after a rule change (none planned; 0116, *Consequences*).
- Retention of the derived rows (#46 names them in *Retention (planned)* of the storage area).
- The spec's *Out of scope*.

## Challenge

Devil's Advocate, plan challenge.

1. **Major: a backfill below a stored start re-decodes the start per record.** Accepted (same as Security B2). Affected starts are collected
   during the batch and computed once each in `WriteListenerComparisons` before the commit, like `WriteTemplates`: at most two decodes per
   affected start per batch (*Approach*, fact 15). Fact 15 gains the case of a start stored first and a 7-day backlog in chronological
   batches; 0116's cost sentence says so; `ListenerMarkWriterDecodesEachStartOncePerBatch` pins the count.
2. **Minor: the tie-break by record ID depends on arrival.** Accepted. The findings are ordered by capture time descending, agent ID, `seq`
   descending, the partial indexes lead with (`captured_at`, `agent_id`, `seq`), and AC8 gains
   `SqliteStoreListenerFindingsSameAtTheCapForEitherOrder`.
3. **Minor: an undecodable stored payload fails every resend.** Accepted with the second choice: that start is `not comparable` and the
   batch is kept, pinned by `SqliteStoreListenerWriteUndecodableBaseIsNotComparable`; a round-trip test of a maximal snapshot pins that it
   does not happen for data the store wrote. Not logged: the storage layer has no logger, and the finding shows the outcome. 0116 and the
   storage area say so.
4. **Minor: two more first-run assertions.** Accepted: `connstats_test.go:705` and `:1399` are in *Test files*, with `inventory` `skipped`.
5. **Minor: the AC1 row contradicts E3.** Accepted: split into two stored starts back to back (two in the tally) and a base, a `seq` gap and
   one start (one in the tally).
6. **Minor: the method is `WriteBatchAsync`.** Accepted, fact 14 corrected.

## Security review

1. **B1: stored `new_listeners` not capped at 64.** Accepted. `ListenerMarkWriter` keeps the first 64 and adds the rest to the stored omitted
   count (a `long`); *Security considerations* corrected; AC7 gains 4,096 entries with omitted 5 → 64 kept, omitted 4,037 (4,032 + 5). The
   comparison (`ListenerComparer`) already caps at 64.
2. **B2: decodes per record in a backfill.** Accepted, as Challenge 1: computed once per affected start per batch, at most two decodes each;
   the claim now reads so (fact 15); the decode counter test runs batches of 10 and 1,000 snapshots below one start and expects 2 for both.
3. **B3: security area 1 is touched.** Accepted: listed under *Security areas touched*; the derived rows are written only for a record whose
   insert returned an ID, only `listener_comparisons` rows are ever updated, and no `records` row; `.squad/project.md` area 1 gains that
   sentence (Dev).

## Security notes

- **N1:** facts 8 and 12 are now confirmed with Security's measurement.
- **N2:** taken: the endpoint is shown as `IPEndPoint.ToString()`, the backend's own form, which differs from the producer's for an
  IPv4-mapped address (fact 18); *Approach* says so, and the spec's E10 now says "numeric form" instead of "the producer's".
- **N3:** decided with Challenge 3: `not comparable` and the batch kept, plus the round-trip test; not a store failure.
- **N4:** taken: the composer splits each finding template at the agent ID's placeholder and inserts the code piece (*Approach*, *Texts*);
  the code-span test asserts the agent ID in every finding text and the head.
