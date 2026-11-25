# Measurements: issue #496

Values as given by the Product Manager on 2026-11-25 (benchmark run on the reference host that day, not re-run), quoted unchanged.

## `--network-view` run on the reference host

```text
Linux 4.4.302+ #86009 SMP Wed Nov 26 18:45:32 CST 2025; .NET 11.0.0; 4 logical CPUs
database below /volume/data/bench/496/; one fixed clock, default periods
N4: 26280 hourly and 8640 5-minute buckets written directly into the tables (3363840 and 673920 rows, not folded) in 67114 ms
panel read: 4 store calls (2 snapshots decoded), comparison snapshot read
30-day range, step 01:00:00, width 1000: 720 steps, 13 states, 11 program lines, 20 addresses shown
30-day range: chart action (sources, states, programs): median 314 ms of 628, 373, 314, 195, 203
30-day range: address action (beside the panel): median 211 ms of 275, 215, 206, 206, 211
30-day range: panel reads beside the address action: median 19 ms of 21, 19, 19, 39, 11
365-day range, step 12:00:00, width 1000: 730 steps, 13 states, 11 program lines, 20 addresses shown
365-day range: chart action: median 1458 ms of 1458, 1533, 1455, 1463, 1450
365-day range: address action: median 2666 ms of 2680, 2643, 2685, 2659, 2666
365-day range: panel reads: median 11 ms of 10, 11, 10, 11, 12
1095-day range, step 2.00:00:00, width 1000: 549 steps, 13 states, 11 program lines, 20 addresses shown
1095-day range: chart action: median 4214 ms of 4250, 4244, 4166, 4196, 4214
1095-day range: address action: median 8034 ms of 8028, 8485, 8065, 8019, 8034
1095-day range: panel reads: median 10 ms of 10, 10, 10, 10, 11
N4: all 6 medians are under 10000 ms: criterion met
```

Host: the reference host. Volume: `/volume/data`, the data volume (a Synology-style NAS, in the Product Manager's words); whether it is
an SSD volume was not stated.

## Browser check on `/network`

The Product Manager's words: "everything works". The issue's three checks (a dragged range zooms both stacked charts, a click loads the
listener panel for that moment, the per-state and per-program charts stack correctly with a working legend) were not reported one by one.

## Observation outside the criteria

After a click on `/report` nothing happens at first; the top tabs are clickable but no page shows; apparently not always reproducible.
Filed as bug issue #501.
