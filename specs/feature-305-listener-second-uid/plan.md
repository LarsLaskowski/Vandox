# Plan: Report a known listener endpoint whose socket UID changes

Source: Issue #305 | [spec.md](spec.md)
Status: Draft
Tier: standard — a new detection signal from the agent through the wire format, storage and the forensics report; it creates, changes or removes no security control (no guard decides what anyone may reach, and the report text keeps the existing escaping).
Security areas touched: 10. Parsing of external input (the new `connection_snapshot` fields and their validation in `internal/model` and `Vandox.Core.Model.ConnectionSnapshot`/`ChangedListener`; stored lists decoded by `ListenerQueries`); 12. Logging and display of external data (the changed-endpoint line of the *New listeners* section: the user name stays a code span cut to 32 bytes, no command is shown); 3. Web UI login (the report page's memory per circuit, a figure `.squad/project.md` states, grows by about 12 MB).

## Problem / root cause

See [spec.md](spec.md), *Problem*. The root cause: `internal/connstats/inventory.go:17-23` keeps only `keys`, and `compare` (lines 41-69) looks
keys up in a set; `src/Vandox.Core/Reports/ListenerComparer.cs:87-88` does the same with `(Proto, Local)`.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The agent's previous inventory holds keys only, so a UID change of a known key is invisible | `internal/connstats/inventory.go:17-23` (`inventory{keys, cut, skipped, tables}`), `compare` lines 41-69; `connstats.go:238-246` builds it from `listeners.sorted()` keys | confirmed |
| 2 | Each inventory entry already carries the lowest UID, `hasUID` and `mixedUIDs` during the run | `internal/connstats/snapshot.go:159-178` (`listenerEntry`, `addUID`) | confirmed |
| 3 | The backend compares a start by `(Proto, Local)` only | `src/Vandox.Core/Reports/ListenerComparer.cs:87-88`, `Key` line 122 | confirmed |
| 4 | Issue claim "a local user can bind a device-bound socket on `0.0.0.0:22`" | Upstream v5.15 `inet_csk_bind_conflict` (`net/ipv4/inet_connection_sock.c` 157-181) and `udp_lib_lport_inuse` (`net/ipv4/udp.c` 140-153); measured on kernel 6.18.44 in a scratch script as root and UID 65534: TCP unbound root listener + device-bound (lo) socket of 65534 on the same port `EADDRINUSE`; UDP the same `EADDRINUSE`; TCP 922 as 65534 `EACCES` (`ip_unprivileged_port_start` 1024); `SO_REUSEPORT` root + 65534 `EADDRINUSE` | refuted |
| 5 | Two UIDs can share an endpoint when both sockets are bound to different devices, or both UDP sockets set `SO_REUSEADDR` | Same script: TCP root@lo + 65534@eth0 on 4023 succeeded, `/proc/net/tcp` showed `uid` 65534 and 0; UDP `SO_REUSEADDR` root + 65534 on 6353 succeeded, `uid` 65534 and 0 | confirmed |
| 6 | Issue claim "it is still not reported as a new listener" | Fact 1 and agent.md *Residuals* ("the endpoint is not reported as a new listener") | confirmed |
| 7 | `fill` stops every later list at the first cut and adds the dropped new listeners to their omitted count | `internal/connstats/snapshot.go:52-61` | confirmed |
| 8 | `inventory` is set from the number of `listeners` sent, so a list placed before `listeners` can only cut `listeners` as `new_listeners` can | `snapshot.go:106` (`inventoryState(inv, len(snap.Listeners))`) | confirmed |
| 9 | Agent findings are selected by `new_count > 0` (index and queries) | `src/Vandox.Storage/SchemaMigrator.cs:182`, `ListenerQueries.cs:55-58` | confirmed |
| 10 | Schema steps are never edited; the latest is 12 | `docs/areas/storage.md:76`; `SchemaMigrator.cs` steps 1-12 | confirmed |
| 11 | Wire format 1.1 and records 0115/0116 are unreleased | `git ls-remote --tags origin`: only `v0.1.0`; `git ls-tree v0.1.0 docs/decisions/` holds neither record; wire-format.md line 402-409 | confirmed |
| 12 | The payload JSON options are reflection-based (`DefaultJsonTypeInfoResolver`), case-insensitive, so a derived listener type needs no registration | `src/Vandox.Core/Model/PayloadRegistry.cs:34-47` | confirmed |
| 13 | A user name on the page costs at most about 3 KiB with its runs, a command about 22.9 KiB | `docs/areas/web-ui.md:395-396`, `LogTextRunsOfAlternatingShortNameRetainBoundedHeap` | confirmed |
| 14 | The new section bound is about 80 MB per circuit (68 MB today + 3,840 changed lines of at most about 3.2 KiB) | to verify: computed from fact 13, (20 + 20 + 20) findings x 64 changed lines x about 3.2 KiB = about 12 MB; the Dev recomputes it against the built line (PID, UID text, previous UID) in step 6 and writes the figure into `docs/areas/web-ui.md` and `.squad/project.md` | unverified |

## Acceptance criteria

The criteria are the spec's (AC1 to AC8).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestCompare_UIDChangeOfKnownEndpointReported` (table: lowest UID differs S1, flag turns true S2, lower UID joins S3, lower socket leaves E1, privileged check on a port below 1024 E5; the result holds the previous UID and flag); `TestCompare_NewEndpointIsNotChanged` (E8) | `internal/connstats/inventory_test.go` |
| AC1 | `TestConnections_ChangedListenerMakesFullSnapshot` (two runs over the in-memory world, the second with another `uid` on a known key: `changed_listeners[0]` has `uid`, `user`, `previous_uid`, `aggregates_only` false); `TestConnections_ChangedListenerWithoutPasswdUsesDecimalUser` (E7) | `internal/connstats/connstats_test.go` |
| AC2 | `TestCompare_UnchangedOrUnknownUIDNotReported` (table: same UID and flag S4, flag turns false E1, `hasUID` false on either side E2); `TestConnections_UnchangedUIDStaysRegular` | `internal/connstats/inventory_test.go`, `internal/connstats/connstats_test.go` |
| AC3 | `TestCompare_ChangeNotReportedOutsideComparedPorts` (table: UDP 40000 E4, `none`, `start`, port 8443 under `privileged` E5) | `internal/connstats/inventory_test.go` |
| AC4 | `TestBuildSnapshot_ChangedListenersCappedAndCounted` (65 changes: 64 listed, omitted 1); `TestFill_ChangedListenersBetweenNewListenersAndListeners` (a budget that cuts inside the changed list: dropped entries add to `changed_listeners_omitted`, `listeners` empty, `inventory` from the list as sent) | `internal/connstats/snapshot_test.go` (new; `snapshot.go` has no test file yet) |
| AC5 | `TestCompare_ChangeReportedAgainAfterReturn` (runs A, B (mixed), C (not), D (mixed): reported in B and D, once each) | `internal/connstats/inventory_test.go` |
| AC6 | Go: `TestConnectionSnapshot_ChangedListenersValidation` (table: list or count with `none`/`start`/absent check → `data.listener_check` `must be all or privileged when changed listeners are reported`; entry without `uid` → `data.changed_listeners[0].uid` `required`; without `previous_uid` → `data.changed_listeners[0].previous_uid` `required`; the listener entry rules apply under the `changed_listeners` path; a valid entry passes). C#: `ConnectionSnapshotValidateChangedListenersRejectsBrokenEntries` (the same table) and `ConnectionSnapshotDeserializeWithoutChangedListenersHasNone`. Shared: new cases in `testdata/wire/decoder-cases.json` (negative omitted count `ErrMalformed`, `0` and `null` read as absent, the field errors above) and one `changed_listeners` entry in the golden batch `testdata/wire/all-kinds.jsonl` (and its `.gz` forms) | `internal/model/connection_test.go`, `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`, existing `internal/wire/golden_test.go`, `internal/wire/decode_test.go`, `tests/Vandox.Core.Tests/WireContractTests.cs` read the fixtures |
| AC7 | `MarkdownReportWriterWriteChangedListenersListsBeforeUid` (English and German: head with `, <m> with a changed socket UID`, the line of the *Approach*, ` (lowest of several)` now and before, no command); `MarkdownReportWriterWriteChangeOnlyFindingKeepsNoNewListenerSentence`; `MarkdownReportWriterWriteChangedListenersRestLine` (64 listed, `and 3 more` below the changes only); `ForensicsReporterReadsChangeOnlyAgentFindingsSeparately` (21 change-only and 20 new agent findings: 20 of each kind kept, the cap note for change-only findings); the golden `testdata/report/forensics-en.md` and `forensics-de.md` gain one finding with a change | `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`, `tests/Vandox.Backend.Tests/ForensicsReporterTests.cs` |
| AC7 | `SqliteStoreListenerHistoryChangeOnlyAgentFindingIsReadAsItsOwnKind` (a mark with `new_count` 0 and changes is listed for kind `AgentUidChanges`, not for `Agent`; a mark with both is `Agent` and carries its changes); `SqliteStoreOpenMigratesListenerTablesToStepThirteen` (a database at step 12 with marks and comparisons: new columns 0/NULL, the old rows read as no changes, the partial index exists) | `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` |
| AC8 | `ListenerComparerCompareReportsChangedUid` (data rows: S6, flag turns true, flag turns false not reported, UID absent on either side, UDP 40000, port 8443 under `privileged`, a duplicate endpoint in a list does not throw, more than 64 → 64 and omitted); `ListenerMarkWriterStoresChangesOfAStart` (the comparison row keeps `changed_count` and the list, `finding` 1 with changes only); `ListenerMarkWriterChangesIndependentOfStoringOrder` (the pair stored in both orders gives the same row) | `tests/Vandox.Core.Tests/ListenerComparerTests.cs`, `tests/Vandox.Storage.Tests/ListenerMarkWriterTests.cs` |

## Approach

**Agent (`internal/connstats`).** `inventory` gains `uids []socketUIDs`, parallel to `keys` (an inventory built without it, as the
existing tests do, has every UID unknown). `col.inventory()` fills it from the sorted entries. `compare` returns, besides `check` and
`added`, the changes: for each key of `cur` that `prev` holds, inside the compared part (`privileged`: ports below 1024) and `reportable`,
with `known` on both sides, a change when `uid` differs or `prev.mixed` is false and `cur.mixed` true; in inventory order, each key at most
once. The lookup of `prev` becomes a map from key to its index (one map, as today). `Collect` makes the run full when `len(changed) > 0`.
`buildSnapshot` builds at most `MaxChangedListeners` (64) entries with `listenerOf` plus `PreviousUID`/`PreviousMixedUIDs`, the rest a
count; `fill` fills `changed_listeners` right after `new_listeners` and adds its dropped entries to `changed_listeners_omitted`. `shorten`
does not halve it (as `new_listeners`). No log line.

**Wire.** `connection_snapshot` gains `changed_listeners` (entries: the listener fields plus `previous_uid` (required, 0 to 4294967295) and
`previous_mixed_uids` (optional, absent when false)) and `changed_listeners_omitted` (>= 1, 0 reads as absent). Rules in Go and C#, after
the listener entry rules of each entry: `uid` absent → `data.changed_listeners[<i>].uid`: `required`; `previous_uid` absent →
`data.changed_listeners[<i>].previous_uid`: `required`; list or count with `listener_check` other than `all`/`privileged` →
`data.listener_check`: `must be all or privileged when changed listeners are reported` (checked after the new-listener rule). The list
counts toward the item limit like `new_listeners`. Format stays 1.1 (unreleased).

**Storage.** Step 13: `connection_marks` and `listener_comparisons` each `ADD COLUMN changed_count INTEGER NOT NULL DEFAULT 0 CHECK
(changed_count >= 0)` and `ADD COLUMN changed_listeners TEXT`; `CREATE INDEX connection_marks_changes ON connection_marks(captured_at DESC,
agent_id, seq DESC) WHERE new_count = 0 AND changed_count > 0`. `ListenerMarkWriter.Write` stores the agent's first 64 changes and their
count (0 unless the check is `all`/`privileged`); `Store` writes the comparison's changes and sets `finding` 1 also when `changed_count > 0`.
`ListenerQueries` reads the two columns for every finding and gains the query of kind `AgentUidChanges` (`new_count = 0 AND changed_count >
0`); kind `Agent` keeps `new_count > 0`. Stored as on the wire, the command included (the report does not show it).

**Report.** `ListenerFindingKind.AgentUidChanges`; `ListenerReport` gains `AgentChangeFindings` and `AgentChangeFindingsTotal`;
`ForensicsReporter` reads the third kind with `ListenerReport.MaxFindings`. Layout (fixed here, both languages):

- The agent's findings of both kinds are listed together after the start findings, ordered as today (agent ID, then `seq`).
- A finding head gains `, <m> with a changed socket UID` (`, <m> mit geänderter Socket-UID`) after `<n> new` when `m` (listed plus omitted
  changes) is at least 1; a change-only finding reads `…: 0 new, 2 with a changed socket UID`.
- Below the head: the new listeners and their `and <n> more` as today, then one item per changed endpoint and its own `and <n> more`.
- A changed endpoint's item: `<proto> <address>:<port>, PID <pid>` (`, PID <pid>` in German too) or `<proto> <address>:<port>, no owner`,
  then today's UID pieces (`UidPieces`, current UID, user, ` (cut)`, ` (lowest of several)`), then `, before: socket UID <uid>` (`, vorher:
  Socket-UID <uid>`) and ` (lowest of several)` when `PreviousMixedUids`.
- The closing sentence is decided by new listeners only: it is written when no finding has `Listeners.Count + Omitted > 0` (today: when no
  finding is compared), so a section with change-only findings still ends with `No new listener found.` or its *compared* form.
- Notes: a third cap note `Only the latest {0} findings of the agent with changed socket UIDs only are listed; {1} earlier ones are left
  out.` (`Nur die letzten {0} Funde des Agenten mit nur geänderten Socket-UIDs sind aufgeführt; {1} frühere fehlen.`), after the agent note.

**Backend comparison.** `ListenerComparer.Compare` also builds the changes over the two stored lists with the same rules: a dictionary of
the earlier list by key (first entry of a key wins, `TryAdd`), each later listener with a `Local`, not ephemeral UDP, in the compared
ports, found in it, with `Uid` on both sides and (`Uid` differs or earlier `MixedUids` false and later true); at most 64 and the rest
counted. Starts compared before step 13 are not recomputed (record 0116, option 11).

## Options considered

1. The UID in the inventory key — rejected (record 0115, options 16 and 20).
2. A change list beside `new_listeners` (chosen) or the changes inside `new_listeners` with a marker — rejected, readers counting new
   listeners would count endpoints that are not new (0115, option 22).
3. Change-only findings under the existing two caps — rejected after the spec challenge for a third cap (0116, option 9).
4. The changed line with the owner's command — rejected for the page memory, about 100 MB per circuit more (0116, option 10). This is a
   spec fix made while planning: AC7 now names the line without the command.
5. Recomputing stored comparisons in the schema step — rejected (0116, option 11).
6. `ChangedListener` as its own class repeating `Listener`'s members instead of deriving from it — rejected: two copies of the endpoint
   rules; `Listener` loses `sealed` and its `Validate` becomes virtual.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go `internal/model` | `connection.go` | `ChangedListener`, two snapshot fields, `checkChangedListeners`, the `listener_check` rule, the item count |
| Go `internal/connstats` | `inventory.go`, `connstats.go`, `snapshot.go` | `socketUIDs`, `uidChange`, `compare`, `MaxChangedListeners`, `lists`, `fill`, `buildSnapshot`, `collection.inventory` |
| `Vandox.Core` | `Model/Listener.cs`, `Model/ChangedListener.cs` (new), `Model/ConnectionSnapshot.cs` | unsealed `Listener`, virtual `Validate`; new type; two properties and their rules |
| `Vandox.Core` | `Reports/ListenerComparer.cs`, `ListenerComparison.cs`, `ListenerFinding.cs`, `ListenerFindingKind.cs`, `ListenerReport.cs` | changes, third kind and its page |
| `Vandox.Storage` | `SchemaMigrator.cs`, `ListenerMarkWriter.cs`, `ListenerQueries.cs` | step 13, write and read of the changes, third query |
| `Vandox.Backend` | `Reports/ForensicsReporter.cs`, `ReportComposer.cs`, `ReportText.cs` | third read, the layout above, the new texts |
| `testdata` | `wire/all-kinds.jsonl` (+ `.gz` forms), `wire/decoder-cases.json`, `report/forensics-en.md`, `report/forensics-de.md` | fixtures (Tester) |

## Signatures (for the Dev's skeleton)

Go `internal/model/connection.go`:

```go
// ChangedListener is a known listener endpoint whose socket UID changed since the producer's previous run.
type ChangedListener struct {
	Listener
	// PreviousUID is the endpoint's lowest socket UID in the previous run.
	PreviousUID *uint32 // JSON tag previous_uid,omitempty
	// PreviousMixedUIDs is true when the endpoint's sockets had several UIDs in the previous run.
	PreviousMixedUIDs bool // JSON tag previous_mixed_uids,omitempty
}
// ConnectionSnapshot gains, after NewListenersOmitted:
	ChangedListeners        []ChangedListener // JSON tag changed_listeners,omitempty
	ChangedListenersOmitted uint32            // JSON tag changed_listeners_omitted,omitempty
func checkChangedListeners(listeners []ChangedListener) error
```

Go `internal/connstats`:

```go
const MaxChangedListeners = 64 // changed listeners reported per run (connstats.go, beside MaxNewListeners)
type socketUIDs struct { uid uint32; known, mixed bool }        // inventory.go
type uidChange struct { key listenerKey; before socketUIDs }    // inventory.go
// inventory gains: uids []socketUIDs (parallel to keys; nil: every UID unknown)
func compare(prev, cur *inventory) (check model.ListenerCheck, added []listenerKey, changed []uidChange)
func buildSnapshot(col *collection, res *runResult, check model.ListenerCheck, added []listenerKey, changed []uidChange, full bool) *model.ConnectionSnapshot
// lists gains: changedListeners []model.ChangedListener; changedOmitted uint32
```

C#:

```csharp
public class Listener : SocketOwner                       // was sealed
    internal virtual FieldError? Validate(string path)     // was non-virtual
public sealed class ChangedListener : Listener             // Vandox.Core.Model
    [JsonPropertyName("previous_uid")] [JsonPropertyOrder(103)] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PreviousUid { get; set; }
    [JsonPropertyName("previous_mixed_uids")] [JsonPropertyOrder(104)] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreviousMixedUids { get; set; }
    internal override FieldError? Validate(string path)
// ConnectionSnapshot
    [JsonPropertyName("changed_listeners")] public List<ChangedListener>? ChangedListeners { get; set; }
    [JsonPropertyName("changed_listeners_omitted")] public uint ChangedListenersOmitted { get; set; }
// ListenerComparer
    public const int MaxChangedListeners = 64;
// ListenerComparison, ListenerFinding
    public IReadOnlyList<ChangedListener> ChangedListeners { get; init; } = [];
    public long ChangedOmitted { get; init; }
// ListenerFindingKind
    AgentUidChanges   // after Agent
// ListenerReport
    public IReadOnlyList<ListenerFinding> AgentChangeFindings { get; init; } = [];
    public long AgentChangeFindingsTotal { get; init; }
// ListenerQueries
    internal static string SerializeChangedListeners(IEnumerable<ChangedListener> listeners)
// ReportText (both languages)
    ListenerChanged = ", {0} with a changed socket UID", ListenerChangedEntry = "{0}, PID {1}", ListenerBeforeUid = ", before: socket UID {0}",
    CapAgentChangeFindingsNote = "Only the latest {0} findings of the agent with changed socket UIDs only are listed; {1} earlier ones are left out."
```

Existing files the skeleton rewrites: none whole; every file above is extended.

## Test files

`internal/connstats/inventory_test.go`, `internal/connstats/connstats_test.go`, `internal/connstats/snapshot_test.go` (new),
`internal/model/connection_test.go`, `tests/Vandox.Core.Tests/ConnectionSnapshotTests.cs`, `tests/Vandox.Core.Tests/ListenerComparerTests.cs`,
`tests/Vandox.Storage.Tests/ListenerMarkWriterTests.cs`, `tests/Vandox.Storage.Tests/SqliteStoreListenerHistoryTests.cs`,
`tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs`, `tests/Vandox.Backend.Tests/MarkdownReportWriterTests.cs`,
`tests/Vandox.Backend.Tests/ForensicsReporterTests.cs`; fixtures under `testdata/wire` and `testdata/report`.

Existing call sites of changed signatures: `compare` in `internal/connstats/inventory_test.go` lines 27 and 258 (two return values become
three) — Tester adapts. `tests/Vandox.Backend.Tests/ScriptedListenerHistoryReader.cs` must answer kind `AgentUidChanges` (an empty page by
default) — Tester. Tests that assert the schema's latest version (`SqliteStoreOpenTests`) — Tester. Tests kept unchanged: the existing
`compare` and `ListenerComparer` cases build inventories or lists without a UID change, so no input is handled differently.

Code a unit test cannot cover: none.

## Areas

Agent, Wire format, Detection, alerts and reports, Storage, Web UI (the page memory figure); see *Documentation updates*.

## Documentation updates

Owner: Dev, after the code is green, against the code.

- `docs/areas/agent.md`, *Connections*: *The listener check* — the changed-listener rule, its ports and exclusions, 64 and the count, that
  it makes a full snapshot; *Regular and full snapshots* (line 416-418, the list of runs that are full) — add a run with a change; *Caps*
  (line 410, "at most 64 new listeners are reported") — add 64 changed listeners; *Socket UIDs* (line 460) — replace "A change of the UID …
  is not a new listener" by a pointer to the rule; *The record always fits* (line 506, the fill order) — insert `changed_listeners` after
  `new_listeners`; *Bounded memory* — the previous inventory's UID per key; *Residuals* (lines 576-579) — replace "a listener whose UID
  changes between two runs is not reported either" with the rule, E9, the bind-conflict facts 4-5 and the two inherited residuals of the
  spec's area list.
- `docs/areas/wire-format.md`: the field list (line 197) and the 1.1 fields (lines 216-244, "seven optional fields" becomes nine) with the
  errors of *Approach*; the version note (lines 402-407).
- `docs/areas/detection-and-reports.md`, *New listeners* (lines 552-565: the changed endpoints, the third cap in *Caps*, the order) and
  *Content and format* (lines 607-621: the head, the line, the closing sentence, the note).
- `docs/areas/storage.md`: the `connection_marks` and `listener_comparisons` rows of the table (line 58 and the comparisons row) and the
  write path (line 118); step 13.
- `docs/areas/web-ui.md` line 395-396 and `.squad/project.md` line 72: the section's bound (fact 14) and the third kind of findings.
- `docs/ARCHITECTURE.md` line 44 and line 229 — the agent reports new and changed listeners; the report reads both. Sweep done: no other
  enumeration names the new-listener fields.

## Architecture check

No guarantee of `.squad/project.md` is weakened: the agent adds no right, no read and no log line; the record stays within its budget;
the decoders keep their limits; the page memory figure grows by about 12 MB (fact 14), stated in the documents, inside the existing
caps. Records 0115 and 0116 are extended and linked from `docs/ARCHITECTURE.md` as today.

## Security considerations

- [ ] Limits: `changed_listeners` counts toward the wire item limit (4,096) before decoding the entries, as `new_listeners`; user names are
  cut to 32 UTF-8 bytes after decoding, before escaping, as today; the stored list is cut to 64 entries.
- [ ] Exceptions: a stored `changed_listeners` text that does not decode raises `StoreException` `store: reading listener findings:
  decoding listeners`, the existing text; no other exception reaches a user.

## Decision records

- `docs/decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md`
  (Accepted, unreleased, extended in place: context, options 20-22, decision, consequence).
- `docs/decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md` (Accepted, unreleased,
  extended in place: options 9-11, decision).

## Out of scope / follow-ups

See the spec's *Out of scope*. No follow-up issue.
