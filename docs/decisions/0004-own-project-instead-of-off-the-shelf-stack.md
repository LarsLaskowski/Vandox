# 0004: Own project instead of an off-the-shelf monitoring stack

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The goal is to understand why a single Plesk server (Ubuntu 22.04, about 2 GB RAM) goes down — typically
memory exhaustion, the OOM killer, then MariaDB, Plesk and mail failing — and to warn early. Answering that
needs metrics, process and connection snapshots, kernel events and logs from the same minutes, correlated
into one incident. The monitored server has little memory to spare, and the backend runs on a Synology NAS
in the home network that is sometimes switched off at night.

## Options considered

1. **Off-the-shelf stack** (e.g. Prometheus + node_exporter + Loki/Promtail + Grafana + Alertmanager, or a
   hosted service) — mature and feature-rich; several daemons on a 2 GB server, several containers on the
   NAS, a pull model that does not fit an agent behind Tailscale with a backend that is sometimes off, and
   the correlation of logs with metrics into an outage timeline still has to be built on top.
2. **Own, purpose-built project** — one small agent and one backend container doing exactly what this
   server needs (forensics first, gapless backfill, deterministic rules); everything must be written and
   maintained by the project.

## Decision

Option 2: Vandox is its own project with its own agent (`vandox-agent`) and backend (`vandoxd`). An export
in Prometheus format is kept as a very late backlog item, not as an architectural goal.

## Consequences

- Footprint, offline behavior and analysis can be shaped for this one server.
- Every collector, the storage, the UI and the alerting have to be built and tested in this repository.
- Interoperability with existing tooling is limited until the Prometheus export exists; nothing in the
  data model may rule such an export out.
