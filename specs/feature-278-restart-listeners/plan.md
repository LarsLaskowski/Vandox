# Plan: Report listeners that appeared while the agent was stopped

Source: Issue #278 | [spec.md](spec.md)
Status: Draft
Tier: standard — a behavior change across the agent (two wire fields and a new `listener_check` value), the storage write path (two
derived tables) and the forensics report (a new section) that creates, changes or removes no security control; it touches the parsing of
the wire format, the display of external text and the report page behind the login.
Security areas touched: 3 (Web UI login: the report page and its download read through a new gated reader, `GatedListenerHistoryReader`,
of the shared `WebReaderGate`, and the page's per-circuit document grows by the section), 10 (Parsing of external input: the wire format's
`connection_snapshot` gains `inventory`, `tables` and the `listener_check` value `start`, validated in `internal/model` and
`Vandox.Core.Model.ConnectionSnapshot`; the storage write path decodes a stored payload), 12 (Logging and display of external data: the
owner `command` of a listener and the agent ID are shown in the report, on the page and in the Markdown download)

## Problem / root cause

See [spec.md](spec.md), *Problem*. The cause in the code: `compare` in `internal/connstats/inventory.go` returns `none` when `prev == nil`
(the first run after a start), and the agent keeps `prev` only in memory (`Connections.prev`, `internal/connstats/connstats.go:78`); no
backend code reads `listener_check` or `new_listeners` beyond validation.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The agent's first run after a start compares nothing and reads `none`, the same value as a skipped line, a changed set of tables and a cut inside the privileged part | `internal/connstats/inventory.go`, `compare`: `prev == nil \|\| prev.skipped \|\| cur.skipped \|\| prev.tables != cur.tables` → `none`; `cutInPrivilegedPart` → `none` | confirmed |
| 2 | The record's `listeners` list is a prefix of the inventory keys, in the same order (privileged ports first) | `collection.inventory()` (`connstats.go:217-223`) and `listenerList` (`snapshot.go:486-493`) both take `col.listeners.sorted()`; `fill` keeps a prefix (`snapshot.go:64-90`); `shorten`/`halve` keep the first half (`snapshot.go:120-140`) | confirmed |
| 3 | The record's list can be shorter than the inventory without the inventory being cut (size budget, safety-net halving), so the stored data alone cannot say whether the privileged part is whole | `fill` stops at the first entry that does not fit (`snapshot.go:51-62`); `shorten` halves `Listeners` (`snapshot.go:128-130`); both set only `aggregates_incomplete`, which has six other causes (`buildSnapshot`, `snapshot.go:469-471`) | confirmed |
| 4 | Wire format 1.1 is unreleased, so 1.1 fields may be added in place | `git ls-remote --tags origin`: `v0.1.0` only; `git merge-base --is-ancestor e2f5099 v0.1.0` fails (the 1.1 commit is not in the release); `internal/wire/wire.go:17` `MinorVersion = 1` | confirmed |
| 5 | No agent code emits the gap cause `agent_not_running` | `grep -rn "GapAgentNotRunning" internal cmd` outside tests: only `internal/model/gap.go:12,47` (constant and validation) | confirmed |
| 6 | `seq` comes from one persistent per-agent counter that increases strictly across restarts and reboots | `docs/areas/agent.md`, *Spool and sending*; `docs/areas/wire-format.md`, *Batch identity (0045)*; the spool that assigns it is #38, not built (contract; implementation #38) | confirmed |
| 7 | The store keeps (agent ID, `seq`) once for agent records, and a duplicate gets no typed row | `SchemaMigrator.cs:34` unique index `records_agent_seq`; `BatchWriter.Write` returns `false` when `ExecuteScalar` yields no id (`BatchWriter.cs:110-113`), before any typed insert | confirmed |
| 8 | A batch is written in one transaction on one connection, so a query of the write path sees the rows written earlier in the same batch | to verify: the Tester's AC8 case "base and start in one batch" against the real store (step 5): the start's comparison names the base written just before it in the same transaction. Read so far: `BatchWriter` holds one `SqliteConnection` and `SqliteTransaction` for all its commands (`BatchWriter.cs:43-56`); SQLite shows a transaction its own uncommitted changes | unverified |
| 9 | Agent snapshots are not stored by any product path today | `docs/areas/agent.md`, *Status* (records dropped until the spool exists); `PortRoutingMiddleware` answers 404 on the ingest listener | confirmed |
| 10 | The new section fits between *Memory consumers* and the outages heading without a change of `IReportWriter` | `ReportComposer.Summary` adds head, summary, patterns, consumers, then `Heading(2, OutagesHeading)` (`ReportComposer.cs:51-69`); `ReportViewBuilder` and `MarkdownReportWriter` render `Summary` blocks whole | confirmed |
| 11 | Each page of the report page is a new run of the whole report | record 0108, option 5; `ReportViewBuilder` keeps the summary of every run | confirmed |
| 12 | `IPEndPoint` equality matches the agent's key (`netip.Addr`, port): `0.0.0.0` and `::ffff:0.0.0.0` differ, equal address and port are equal | to verify: the Tester's AC5 rows with `0.0.0.0:23` against `[::ffff:0.0.0.0]:23` (step 5). Read so far: .NET `IPEndPoint.Equals` compares `Address` and `Port`; `IPAddress.Equals` compares family and bytes (scope ID too, which the wire forbids, `wire-format.md`, *Producer mapping*) | unverified |
| 13 | A finding's worst-case text: 64 listeners × a command of 1,024 UTF-8 bytes (up to 1,024 UTF-16 characters, 2 KiB) plus about 100 bytes of fields ≈ 140 KB; 20 + 20 findings ≈ 5.5 MB in the run and in the page's document; the head of 64 agents is under 64 KB | arithmetic from `ModelLimits` short text (1,024 bytes), the caps of this plan and `docs/areas/web-ui.md` (per-circuit amounts) (arithmetic) | confirmed |
| 14 | The section adds at most 5 seconds to a report run over one year of snapshots (525,600 marks, 100 starts, 10,000 agent findings) | to verify: the Dev measures the three reads of the section (`ListenerTalliesAsync` and both `ListenerFindingsAsync`) on the development host over a database of that size written through `SqliteStore.WriteAsync`, in step 6, and writes the figure into *Limits of the report*. Read so far: the section reads only the narrow derived tables (*Approach*) | unverified |
| 15 | The write path adds one index lookup per stored agent connection snapshot and one payload decode (at most 1 MiB of JSON) per start | to verify: the Dev runs `WriteThroughputTests` before and after in step 6 and reports both medians. Read so far: *Approach*, write path | unverified |
| 16 | The issue's "both inventories are complete" means "the list holds every listener", not the wire field `complete` | spec, open question 2 | confirmed |

## Acceptance criteria

The criteria are those of [spec.md](spec.md), AC1 to AC14.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestCompare_FirstRunReportsStart` (the existing `TestCompare_BaselineReportsNone`, renamed and changed to `start`; a later run without a skipped line still compares); `SqliteStoreListenerFindingsComparesStartWithPreviousBySeq` (rows: base and start of one agent; a failed first run, i.e. a `seq` gap without a snapshot; two starts without a snapshot between: the second compared with the first start's snapshot, two starts in the tally; two agents with interleaved `seq` values compared only within each agent) | `internal/connstats/inventory_test.go`; `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC2 | `TestInventoryState` (rows: no cut → `all`); `ListenerComparerCompareWholeInventoriesComparesAllPorts` (S1 `tcp 0.0.0.0:23` and S2 `tcp6 [::]:8443` both new, outcome all ports) | `internal/connstats/inventory_test.go`; `tests/Vandox.Core.Tests/ListenerComparerTests.cs` |
| AC3 | `TestInventoryState` (rows: inventory cut at 4,096 with unprivileged keys kept → `privileged`; a size cut of the record after the privileged part → `privileged`); `TestConnections_SizeCutOfListenersSetsInventory`; `ListenerComparerCompareCutInventoryComparesPrivilegedOnly` (either side `privileged`: new `0.0.0.0:25` listed, new `0.0.0.0:8443` not) | `internal/connstats/inventory_test.go`; `internal/connstats/connstats_test.go`; `tests/Vandox.Core.Tests/ListenerComparerTests.cs` |
| AC4 | `TestInventoryState` (rows: a skipped line → `skipped`, also with a cut; cut inside the privileged part of the inventory → `cut`; a size cut inside the privileged part → `cut`); `TestConnections_SnapshotCarriesInventoryAndTables` (all four tables, and without `tcp6`/`udp6`); `ListenerComparerCompareWithoutBaseIsNoEarlierInventory`; `ListenerComparerCompareSkippedOrOtherTablesIsNotComparable` (rows: `skipped` before, `skipped` after, tables differ); `ListenerComparerCompareCutPrivilegedPartIsPrivilegedIncomplete` (rows: `cut` before, `cut` after; `skipped` wins over `cut`); `MarkdownReportWriterWritesNotComparedReasons` (three reasons × `en`/`de`, exact texts of AC11) | `internal/connstats/inventory_test.go`; `internal/connstats/connstats_test.go`; `tests/Vandox.Core.Tests/ListenerComparerTests.cs`; `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC5 | `ListenerComparerCompareOwnerChangeIsNotNew`; `ListenerComparerCompareEphemeralUdpNotReported` (rows: `udp` and `udp6` on 32767 listed, 32768 and 60999 not, 61000 listed; `tcp` on 40000 listed); `ListenerComparerCompareMappedAddressIsAnotherListener` (`[::ffff:0.0.0.0]:23` after `0.0.0.0:23` is new); `SqliteStoreListenerFindingsComparedWithoutNewListenerIsNoFinding` (counted in the tally, not listed) | `tests/Vandox.Core.Tests/ListenerComparerTests.cs`; `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC6 | `ListenerComparerCompareListsAtMost64InInventoryOrder` (70 new: the first 64 in inventory order, omitted 6, owner PID and command from the later snapshot, none when the entry has no PID); `MarkdownReportWriterWritesRestOfNewListeners` (`and 6 more` / `und 6 weitere`) | `tests/Vandox.Core.Tests/ListenerComparerTests.cs`; `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC7 | `SqliteStoreListenerFindingsKeepsAgentFindings` (a snapshot with `listener_check` `privileged`, two `new_listeners` and `new_listeners_omitted` 3: one agent finding with both listeners, omitted 3, outcome ports 1 to 1023 only; a snapshot with `all` and none: no finding) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC8 | `SqliteStoreListenerFindingsSameForAnyStoreOrder` (rows: base and start in one batch; the start in one batch, its base in a later one; an older base, then the start, then a base between them in a later batch, which replaces the comparison; the same batch written twice; each row ends with the same tally and findings); `SqliteStoreListenerFindingsOrderBySeqWhenTheClockStepsBack` (start captured before its base: still compared with it; findings of one agent returned in `seq` order by the composer) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC9 | `SqliteStoreListenerFindingsIgnoresOtherSnapshots` (rows: origin `import` source `lsof`; origin `agent` source `connections` without `listener_check`; without `inventory`; without `tables`; another source: no mark, no comparison, not in the tally); `TestConnectionSnapshot_Validate` (new rows: `start`, each `inventory` value, `tables` with a bad protocol and a duplicate); `ConnectionSnapshotValidateAcceptsTheNewFields` and `ConnectionSnapshotValidateRefusesBrokenFieldRules` (the same rows in C#) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`; `internal/model/connection_test.go`; `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs` |
| AC10 | `MarkdownReportWriterWritesCommandAndAgentIdAsCodeSpans` (a command with `<b>x</b>`, a run of three backticks, U+202E and a line feed: written through `MarkdownText.Code`, the escapes `‮` and `\n` present in the output; the test asserts that its expected text still holds the backslash, *Escapes in files*); `ReportContentShowsListenerCommandAsText` (the page: `<code>` with the visible escape, no element from the command) | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`; `tests/Vandox.Backend.Tests/ReportContentTests.cs` |
| AC11 | `MarkdownReportWriterWritesNewListenersBeforeOutages` (rows `en`, `de`: heading, head line, a start finding and an agent finding with the phrases of the table, position after *Memory consumers* and before *Outages*); `MarkdownReportWriterWritesNoNewListenerVariants` (rows: no agent snapshot → no-data text; starts all compared and no finding → `No new listener found.`; a start not compared → its finding and `No new listener found among the compared inventories.`; both languages); `ForensicsReporterRunWritesListenerSectionFromStore` (S1 end to end on a real store); `ReportContentShowsListenerSectionLikeTheMarkdown` (page pieces equal the Markdown pieces); the golden reports `testdata/report/forensics-{en,de}.md` regenerated with the section | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`; `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs`; `tests/Vandox.Backend.Tests/ReportContentTests.cs` |
| AC12 | `SqliteStoreListenerTalliesListsAtMost64Agents` (65 agents: the 64 lowest IDs, 1 omitted); `MarkdownReportWriterNotesListenerCaps` (the three notes in both languages) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`; `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs` |
| AC13 | `SqliteStoreListenerFindingsCapsEachKindSeparately` (1,000 agent findings and one start finding on `tcp 0.0.0.0:23` older than all of them: the start page holds it, total 1; the agent page holds the latest 20, total 1,000) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |
| AC14 | `SqliteStoreListenerFindingsReadsNoPayload` (after the writes the test overwrites `records.data` of every stored snapshot with `{}` by SQL; tallies and both finding pages are unchanged); the time bound by the Dev's measurement of *Verified facts* row 14 | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` |

## Approach

**Agent (Go).** `compare` returns `model.ListenerCheckStart` when `prev == nil` (all other `none` cases stay). Every snapshot carries
`Tables` (the protocols of the tables read, in table order, from `col.read`) and `Inventory`, the state of the record's own `listeners`
list, computed by `inventoryState` from the run's inventory and the length of the list as it finally stands: `skipped` when a table line
was skipped; `cut` when the inventory was cut inside its privileged part or the list holds fewer entries than the inventory has on ports
below 1024; `all` when the inventory was not cut and the list holds every key; `privileged` otherwise. `fitRecord` sets it before every size
check, because `shorten` can halve the list. Nothing else changes: the agent's own comparison, full snapshots and caps stay.

**Wire and model (Go and C#).** `listener_check` accepts `start`; `inventory` is one of `all`, `privileged`, `cut`, `skipped`; `tables` holds
distinct protocols (`tcp`, `tcp6`, `udp`, `udp6`). All optional and omitted when empty; `new_listeners` still needs `all` or `privileged`.
Field errors: `data.listener_check` and `data.inventory` with the model's one-of text, `data.tables[<i>]` with the protocol text of the
other lists, and `duplicate protocol`. The golden batch gains both fields and a `start` snapshot (record 0075).

**Comparison (C#, `Vandox.Core.Reports.ListenerComparer`).** A pure function of the earlier and the later `ConnectionSnapshot`, with the
agent's rules in this order: no earlier → no earlier inventory; `skipped` on either side, or other `tables` → not comparable; `cut` on
either side → ports 1 to 1023 not complete; both `all` → all ports; otherwise ports 1 to 1023 only (ports below 1024, as the agent). Keys
are (`proto`, `local`); a key of the later list that the earlier lacks is new unless it is `udp`/`udp6` on 32768 to 60999; the first 64 in
the later list's order are kept with their PID and command, the rest counted.

**Storage (C#).** Schema step 11 adds `connection_marks` (one row per usable agent snapshot: `record_id`, `agent_id`, `seq`, `captured_at`,
`listener_check`, `new_count`, `new_listeners` JSON for an agent finding) with a unique index on (`agent_id`, `seq`) and a partial index for
findings, and `listener_comparisons` (one row per start: the start's `record_id`, `agent_id`, `seq`, `captured_at`, the base's `record_id`,
`seq` and `captured_at` or `NULL`, `outcome`, `new_count`, `new_listeners`, `finding`) with a partial index for findings. A snapshot is
**usable** when its origin is `agent`, its source `connections`, it has an agent ID, `listener_check`, `inventory` and at least one table.
`BatchWriter.Write` hands a stored usable snapshot to `ListenerMarkWriter.Write` in the same transaction: it inserts the mark; for a start
it reads the agent's previous mark by `seq`, decodes that record's payload and stores the comparison; for every mark it looks up the next
mark by `seq` and, when that is a start, recomputes its comparison with this snapshot (decoding the start's stored payload), so a base that
arrives later replaces the older one. A duplicate record writes nothing (fact 7). The reads of the report: `ListenerTalliesAsync` (per
agent ID, the 64 lowest: marks, first and last capture time, starts per outcome; and the number of further agent IDs) and
`ListenerFindingsAsync` (of one kind, the latest 1 to 20 by capture time, then record ID, and the total), reading only the derived tables.

**Report (C#).** `ForensicsReporter` reads the tallies and the two finding pages after the incidents' first pass and sets
`ForensicsSummary.Listeners`; `ReportComposer.Summary` adds the section after the consumers, findings sorted by agent ID, then `seq`;
`Notes` adds the cap notes. The texts are in `ReportText` (both languages, *Texts* below). The agent ID and the command are
`ReportPiece.Code`; protocol, endpoint (`IPEndPoint.ToString()`, the producer's numeric form), PID, times and counts are plain.
`ReportService`, `ReportCommand` and `ServeCommand` pass the new reader; the web goes through `GatedListenerHistoryReader` (one slot of the
shared `WebReaderGate` per call, as `GatedIncidentStore`).

**Texts** (`{0}`… are the composer's plain pieces; the agent ID and the command are code spans):

| Key | English | German |
| --- | ------- | ------ |
| `NewListenersHeading` | `## New listeners` | `## Neue Listener` |
| `ListenerAgentHead` | `Agent {0}: {1} connection snapshots from {2} to {3}; {4} agent starts: {5} compared on all ports, {6} on ports 1 to 1023 only, {7} not compared` | `Agent {0}: {1} Verbindungs-Snapshots von {2} bis {3}; {4} Agentenstarts: {5} auf allen Ports verglichen, {6} nur auf Ports 1 bis 1023, {7} nicht verglichen` |
| `ListenerStartFinding` | `{0}, agent {1}, after an agent start (compared with {2}), {3}: {4} new` | `{0}, Agent {1}, nach einem Agentenstart (verglichen mit {2}), {3}: {4} neu` |
| `ListenerStartNotCompared` | `{0}, agent {1}, after an agent start, not compared: {2}` | `{0}, Agent {1}, nach einem Agentenstart, nicht verglichen: {2}` |
| `ListenerAgentFinding` | `{0}, agent {1}, found by the agent, {2}: {3} new` | `{0}, Agent {1}, vom Agenten gefunden, {2}: {3} neu` |
| `ListenerAllPorts` / `ListenerPrivilegedOnly` | `all ports` / `ports 1 to 1023 only` | `alle Ports` / `nur Ports 1 bis 1023` |
| `ListenerNoEarlier` / `ListenerPrivilegedIncomplete` / `ListenerNotComparable` | `no earlier inventory` / `ports 1 to 1023 not complete` / `inventories not comparable` | `kein früheres Inventar` / `Ports 1 bis 1023 unvollständig` / `Inventare nicht vergleichbar` |
| `ListenerEntry` / `ListenerEntryNoOwner` | `{0} {1}, PID {2} {3}` / `{0} {1}, no owner` | `{0} {1}, PID {2} {3}` / `{0} {1}, kein Besitzer` |
| `ListenerRest` | `and {0} more` | `und {0} weitere` |
| `NoNewListener` / `NoNewListenerCompared` | `No new listener found.` / `No new listener found among the compared inventories.` | `Kein neuer Listener gefunden.` / `Kein neuer Listener unter den verglichenen Inventaren gefunden.` |
| `NoAgentSnapshots` | `No agent connection snapshot stored: new listeners unknown.` | `Kein Verbindungs-Snapshot eines Agenten gespeichert: neue Listener unbekannt.` |
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
}
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

Store errors: an undecodable `new_listeners` value is `store: reading listener findings: decoding listeners`; an undecodable stored payload
in the write path is the existing `store: reading records: decoding connection_snapshot payload`, which fails the batch.

## Test files

- `internal/connstats/inventory_test.go`, `internal/connstats/connstats_test.go`, `internal/model/connection_test.go`,
  `internal/wire/golden_test.go` (fixture regenerated with `VANDOX_UPDATE_GOLDEN=1`)
- `tests/Vandox.Core.Tests/ListenerComparerTests.cs` (new), `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`,
  `tests/Vandox.Core.Tests/WireContractTests.cs` (the golden batch's new values)
- `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs` (new), `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` (schema version
  11)
- `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`, `ReportContentTests.cs`, `ForensicsReporterTests.cs`,
  `GatedListenerHistoryReaderTests.cs` (new, coverage pass: one slot per call, released on failure)

Existing test code that calls a changed signature, adapted by the Tester: `TestCompare_BaselineReportsNone` and the `nil`-previous calls
of `checkCompare` (`inventory_test.go:79,82`), `wantFlags(…, model.ListenerCheckNone)` of a first run (`connstats_test.go:288,539,660`, each
checked: first run → `start`); every `fitRecord` call in tests; `new ForensicsReporter(…)` in `ForensicsReporterTests.cs:372`; `new
ReportService(…)` in `ReportServiceTests.cs:154`, `ReportPageHarness.cs:61` and `ReportDownloadEndpointTests.cs` (its `Service` helper);
`ReportSamples` (sets `Listeners` for the rich sample); the golden files `testdata/report/forensics-{en,de}.md`; a schema-version assertion
in `SqliteStoreOpenTests`. A test double for the new reader where a test uses `ScriptedRecordReader`: `ScriptedListenerHistoryReader`
(scripted tallies and pages, copies of every query with its token), `tests/Vandox.Backend.Tests`.

Files no unit test can cover: none.

## Areas

- [Agent](../../docs/areas/agent.md), *Connections*: the listener check's `start`; `inventory` and `tables` with their rules; the restart
  residual points to the report.
- [Wire format](../../docs/areas/wire-format.md), *`connection_snapshot`*: `start`, `inventory`, `tables`, their field errors.
- [Storage](../../docs/areas/storage.md), *Schema* (version 11, both tables, their indexes), *Writing* (the marks and comparisons in the
  write path), *Reading* (the two reads and their limits), *Retention (planned)* (the derived rows go with their records).
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Forensics report*: *What it reads*, a new *New listeners*
  subsection (usable snapshots, the outcomes, the rule for a new listener, the agent's findings, order by `seq`, caps), *Content and format*
  (the section, its texts), *External text in the report* (command and agent ID), *Limits of the report* (memory, time, residuals).
- [Web UI](../../docs/areas/web-ui.md), *Report page*: the section, the gated reader, the per-circuit amount.

## Documentation updates

Sweep done for every enumeration the change extends: `listener_check (all, privileged, none)` (`wire-format.md:179,189`), the schema
version and table list (`storage.md:36-63`), the report's reads (`detection-and-reports.md:407,415-424,585`, `ARCHITECTURE.md:171-172`,
`web-ui.md:273` prerendered text, checked: it names only log lines and stays), `#278` (`agent.md:465`), the security areas and test doubles
of `.squad/project.md`.

| Document | Update | Owner |
| -------- | ------ | ----- |
| `docs/areas/agent.md` | as *Areas* | Dev |
| `docs/areas/wire-format.md` | as *Areas* | Dev |
| `docs/areas/storage.md` | as *Areas* | Dev |
| `docs/areas/detection-and-reports.md` | as *Areas*, with the measured time of fact 14 | Dev |
| `docs/areas/web-ui.md` | as *Areas* | Dev |
| `docs/ARCHITECTURE.md` | *Data flow*, the forensics report paragraph: it also reads the stored listener comparisons; link 0116 | Dev |
| `docs/BENCHMARKS.md` | the section's measurement (fact 14) | Dev |
| `.squad/project.md` | area 3 (the report reads through `GatedListenerHistoryReader` too; the page's per-circuit document grows by at most about 5.5 MB), area 10 (the new fields), area 12 (command and agent ID of the section only through `MarkdownText.Code` and `ExternalText`) | Dev |
| `.squad/project.md` | *Test doubles*, database row: `ScriptedListenerHistoryReader` | Tester |
| `docs/decisions/0116-…md`, index, 0115 residual | written | Lead (done) |

## Architecture check

The three guarantees of `.squad/project.md` hold: an uncompared start says why and an unknown never reads as "no new listener" (no gaps);
the collector does no new I/O (never blocks); no alert is added (backfill). The agent stays outbound-only, unprivileged and without a new
file; the backend gets no new listener or route. `ARCHITECTURE.md` links 0116 (Dev).

## Security considerations

- [x] Limits on parsed input: `tables` holds at most four distinct protocols (the distinct rule, checked after decoding, like every list
  limit); `inventory` is an enumeration; the stored `new_listeners` JSON is at most 64 entries the wire already validated, read back through
  the payload options (the decoder's limits apply as for any payload).
- [x] Exceptions that reach a user: a store failure of the section's reads is a `StoreException` and shows the page's existing failure text
  (`The report could …`, web UI area) and exit 1 of `vandoxd report`; the log holds the exception only. A failure of the write path fails the
  batch with its `StoreException` text (`store: reading records: decoding connection_snapshot payload`).
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
