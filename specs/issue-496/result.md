## Tier and result for issue #496

**Tier: `docs`.** The PM's results from the reference host are in, and the only edit they need is in `docs/BENCHMARKS.md`. I swept `docs/areas/` (web-ui-network-view.md, connection-rollups.md, web-ui.md, storage-reading.md), README.md and CHANGELOG.md, and none of them says the reference-host run is pending. `docs/areas/web-ui-network-view.md` (*Limits and errors*, *Time*) says both actions "each finish within the deadline: see Benchmarks, *Network view*", and these figures now back that up. So no area document changes, and the run stays out of `docs/areas/`, which would raise the tier. No decision record is needed. Security areas touched: none.

**Written:** `/home/user/Vandox/specs/issue-496/measurements.md` holds the PM's tool output verbatim. Under it are the volume as given, the browser-check quote as given and the `/report` observation (#501).

### Flags
1. **SSD wording.** The issue asks for a run "on the SSD data volume", and the *Procedure* of *Network view* says "(the SSD volume)". The PM gave only the path `/volume/data` and did not confirm SSD. The row therefore says "data volume `/volume/data`" and does not claim SSD. The 2026-11-22 *Processes view* rows for the same host say "SSD volume". If the PM confirms SSD, "data volume" in the three cells can become "SSD volume, `/volume/data`".
2. **Not recorded because not given:** the build's commit (the *Processes view* rows name a branch head) and whether the build was self-contained. The kernel goes into the table shortened to "Linux 4.4.302+"; the full string is in measurements.md.
3. **AC10 vs N4.** AC10 of #51 also covers the browser check. The PM said only "everything works" and did not go through the three checks one by one. So the verdict cell says **N4 met** (the tool's printed verdict), and the browser check appears only as that quote. The edit does not claim each of the three checks or "AC10 met".
4. **#501** is not mentioned in any product document. It is outside the criteria and belongs to the Report page, not to *Network view*.

### Exact edits: `docs/BENCHMARKS.md`, section *Network view*, *Results*

**Edit 1.** Replace the pending row (line 476):
```
| Reference host | pending | SSD volume | | 30 days, 1 year, 1,095 days | 1 hour, 12 hours, 2 days | **not yet measured** | **not yet measured** | **not yet measured** | **pending: run by the Product Manager** |
```
with these three rows:
```
| Reference host (Linux 4.4.302+, .NET 11.0.0, 4 logical CPUs) | 2026-11-25 | data volume `/volume/data` | written by SQL in 67,114 ms (3,363,840 hourly and 673,920 5-minute rows) | 30 days | 1 hour | 314 ms | 211 ms | 19 ms | **N4 met** |
| Reference host (as above) | 2026-11-25 | data volume `/volume/data` | (same database) | 1 year | 12 hours | 1,458 ms | 2,666 ms | 11 ms | **N4 met** |
| Reference host (as above) | 2026-11-25 | data volume `/volume/data` | (same database) | 1,095 days | 2 days | 4,214 ms | 8,034 ms | 10 ms | **N4 met** |
```

**Edit 2.** Replace the closing sentence of the paragraph under the table (lines 479–480):
```
The tool printed that all 6 medians are under 10,000 ms. The
reference-host row stays pending until the run of the Product Manager is recorded here; until then N4 is not shown on the reference host.
```
with:
```
The tool printed that all 6 medians are under 10,000 ms.

**Outcome on the reference host (Product Manager, 2026-11-25): N4 met.** The run, under one fixed clock and the default periods with the
database below `/volume/data/bench/496/`, wrote the 26,280 hourly and 8,640 5-minute buckets directly into the tables (3,363,840 and 673,920
rows, not folded) in 67,114 ms and printed "panel read: 4 store calls (2 snapshots decoded), comparison snapshot read". At a width of 1,000
the three ranges had 720, 730 and 549 steps, each with 13 states, 11 program lines and 20 addresses shown. The medians are of five reads
each (30 days: chart action 628, 373, 314, 195, 203 ms, address action 275, 215, 206, 206, 211 ms, panel reads 21, 19, 19, 39, 11 ms; 1 year:
1,458, 1,533, 1,455, 1,463, 1,450 ms, 2,680, 2,643, 2,685, 2,659, 2,666 ms and 10, 11, 10, 11, 12 ms; 1,095 days: 4,250, 4,244, 4,166, 4,196,
4,214 ms, 8,028, 8,485, 8,065, 8,019, 8,034 ms and 10, 10, 10, 10, 11 ms). The tool printed that all 6 medians are under 10,000 ms, criterion
met; the largest is the address action of the 1,095-day range, 8,034 ms. The browser check on `/network` by the Product Manager: "everything works".
```
Nothing else in the file changes. The *Procedure* text, including its "(the SSD volume)", stays as it is (see flag 1).

### Acceptance criteria (for the Reviewer)
1. The pending reference-host row is gone. Three reference-host rows (30 days / 1 hour, 1 year / 12 hours, 1,095 days / 2 days) show exactly these figures:
   - chart action: 314, 1,458 and 4,214 ms
   - address action: 211, 2,666 and 8,034 ms
   - panel reads: 19, 11 and 10 ms
   - date 2026-11-25, volume `/volume/data`, data set written in 67,114 ms (3,363,840 / 673,920 rows)
2. Every figure in the outcome paragraph matches `specs/issue-496/measurements.md` digit for digit, with only thousands separators and step notation (01:00:00 → 1 hour, 12:00:00 → 12 hours, 2.00:00:00 → 2 days) changed.
3. No SSD claim in the new rows, no figure beyond the PM's output, and the browser check quoted only as "everything works".
4. The sentence "The reference-host row stays pending … until then N4 is not shown on the reference host." is removed.
5. `scope-check.py --tier docs` and *Format check* pass. The diff outside `specs/` touches only `docs/BENCHMARKS.md`.

RESULT: DONE
