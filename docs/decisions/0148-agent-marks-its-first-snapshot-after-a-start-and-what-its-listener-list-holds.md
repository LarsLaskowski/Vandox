# 0148: The agent marks its first connection snapshot after a start and states what each snapshot's listener list holds

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Listener changes
- **Source:** Issue #278
- **Supersedes:** —

## Context

To compare listeners across an agent start ([0116](0116-listeners-across-agent-starts-compared-when-stored.md)) the backend must
know which stored snapshot is the first after a start and whether two snapshots' listener lists can be compared. The stored data
could not tell: `listener_check` `none` has three other causes (a skipped line, a changed set of tables, a cut privileged part), the
boot ID changes only on a reboot, and a list cut by the record's size budget or the inventory cap may or may not still hold every
listener on ports 1 to 1023 (`aggregates_incomplete` has other causes too). Wire format 1.1 was unreleased.

## Options considered

1. **The backend infers a start from the stored data** (`listener_check` `none` on a full snapshot, a new boot ID, the agent's CPU
   time falling) — no agent change, but `none` also follows a skipped line, a changed set of tables or a cut privileged part, a restart
   within one boot keeps the boot ID, and the self-metrics are another record kind. Rejected: it would compare snapshots the agent
   itself refused to.
2. **The gap cause `agent_not_running`**, already in the model and accepted from the agent, emitted at a start — no new field, but
   the agent would need the time it stopped, which only persistent state can give, the gap is another record that must be matched to
   the snapshot, and it says nothing about what an inventory holds. Rejected.
3. **Fields of `connection_snapshot`** (chosen): `listener_check` gains the value `start` for a run that had no previous inventory,
   and two fields say what the record's list holds (`inventory`) and which tables were read (`tables`). They are local to the record
   the comparison reads, cost a few bytes, and 1.1 was unreleased, so they extend it in place.

## Decision

The agent marks its first connection snapshot after a start with `listener_check` `start` and states in every snapshot what its
listener list holds and which tables it read. The rules are in [Agent connections](../areas/agent-connections.md) (*The listener
check*, *What a snapshot's own list holds*) and the fields in [Wire format records](../areas/wire-format-records.md)
(*`connection_snapshot`*).

## Consequences

- The agent's first snapshot after a start no longer reads `none` but `start`; readers that treated `none` as "not compared" treat
  `start` the same way.
- Neither field changes the agent's own comparison, the full snapshots or the caps.
