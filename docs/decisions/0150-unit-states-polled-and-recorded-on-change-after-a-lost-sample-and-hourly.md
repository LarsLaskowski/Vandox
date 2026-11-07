# 0150: Service unit states are polled and recorded on change, after a start or a lost sample, and hourly

- **Status:** Accepted
- **Date:** 2026-10-20
- **Area:** Agent services and backup runs
- **Source:** Issue #34
- **Supersedes:** —

## Context

#34 asks for service state changes as timestamped events. The `systemd` collector reads the unit states over a short-lived
connection per run ([0117](0117-systemd-collector-hand-written-d-bus-client-one-connection-per-run.md)). A lost sample is a gap
(0028), and the analysis reads the services' states inside an incident's time window, so a quiet unit should still leave a record
now and then. The default list of Plesk services makes about 1,300 records an hour if every unit is recorded every run.

## Options considered

1. **Noticing changes: subscribing to `PropertiesChanged`** — instant notice, but a persistent connection and match rules that make
   the bus deliver every unit's broadcasts to the agent.
2. **Polling at the collector's interval** (chosen), shorter under memory pressure, with `NRestarts` and `ActiveEnterTimestamp`
   revealing a restart between two runs.
3. **What to record: every unit every run** — about 1,300 identical records per hour for the default list.
4. **Only changes** — a state lost with a dropped sample is never recorded, and a quiet unit leaves no record in a time window.
5. **Changes, every unit after a start and after a lost sample, and every unit again once its last record is an hour old**
   (chosen).

## Decision

The collector polls the units every run and records a unit's state on a change, after the agent's start or a lost sample, and once
its last record is an hour old. The rules are in [Agent services and backup runs](../areas/agent-services.md) (*Services*, **What is
recorded and when**).

## Consequences

- A unit's records are at most an hour plus one interval apart; a state that comes and goes between two runs without changing
  `NRestarts` or `ActiveEnterTimestamp` is not seen, and the second of a failure comes from the journal.
- Self-healing (#69) can read the same states; a need for instant notice of a change would revisit option 1.
