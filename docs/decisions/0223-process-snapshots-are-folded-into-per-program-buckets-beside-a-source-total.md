# 0223: Process snapshots are folded into per-program buckets beside a source total, by record ID

- **Status:** Proposed
- **Date:** 2026-11-21
- **Area:** Storage
- **Source:** Issue #458
- **Supersedes:** —

## Context

Retention removes process snapshots after `retention.snapshots` (30 days by default), and only metric values leave rollups behind
([0208](0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md)). The processes view (#50) needs the
memory and CPU of each program over any period, stacked, so a step's programs must add up to the source's total, and it must know
whether memory is PSS or RSS. A snapshot names up to 4,096 programs by free text (1,024 bytes each), and a program absent from a
snapshot used nothing in it. The write path carries the throughput criterion of record 0083.

## Options considered

1. **Per-program metric series in `metric_rollups_5m`/`_1h`** (a name per measure, the program as a label) — reuses the fold and the
   time-series query, but a bucket keeps no snapshot count, so a missing program cannot count as 0, no step knows whether all its
   snapshots had PSS, and hostile names would fill the label sets of every metric reader.
2. **Per-program tables beside a source total per bucket, folded from new snapshots by record ID** (chosen) — the total row counts the
   snapshots, the PSS snapshots and the incomplete ones and holds the sums of every program, so a missing program reads as 0, *other
   programs* is the total minus the named ones, and cut or capped names lose nothing of the total; it merges like record 0208.
3. **Hourly buckets only** — a quarter of the disk, but a day's view within the last year has no 5-minute steps, unlike the history
   view's metric lines.
4. **Fold in the write path** — rejected for the reasons of record 0208 (cost on every batch and every import).
5. **A separate store call and job loop for the program fold** — isolates a failing program fold from the metric fold, but changes
   `IRetentionStore` and every test double and helper of it, while an undecodable snapshot, the one lasting cause, is skipped anyway.

## Decision

The metric fold step also folds the process snapshots above a watermark of their own (`program_rollup_after_id`, by record ID) into a
5-minute and an hourly row per source, bucket and named program and one total row per source and bucket, in the same transaction; a
process snapshot is removed only at or below that watermark. The rules (the cuts, the caps, the read) are in
[Program rollups](../areas/program-rollups.md).

## Consequences

- A snapshot whose data does not decode is skipped and counted, so it never stops the fold or the removal of snapshots.
- The fold step does more work (a metric step and a program step in one transaction); its bound is measured against an ingest batch
  (`docs/BENCHMARKS.md`).
- A program outside a snapshot's cut or a bucket's cap reads lower than it was, without a flag; the total stays exact.
- The 5-minute tier costs about as many rows per day as the named programs times 288; the measured growth is in the area document.
- Revisit if a view needs per-program process counts, the last value or a percentile: none of them is stored.
