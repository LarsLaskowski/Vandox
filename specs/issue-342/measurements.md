# Measurements of issue #342

The values the Product Manager measured on the real systems and handed over on 2026-11-09, transcribed from the
orchestrator's brief and the two raw files (`vandox-messung.txt`: agent readings, `backend-stats.txt`: `docker stats`
during the clean backfill). This file is the Dev's only source for numbers (`plan.md`, *Approach*). **Measured**
values are entered exactly; values marked *(derived)* are computed from them and are written as derived in the
documents. Nothing here may be extended by further computation in the documents beyond what this file states.

## Setup

- Agent `v0.2.0-rc.1` on the production server: Ubuntu 22.04, kernel 5.15, 1 core, 1,963 MB, CPU "AMD EPYC Processor (with
  IBPB)", 174 processes; `agent_id` `web`; `backend.url` the backend's tailnet address; default `backend.backfill_bandwidth`
  `256KiB`.
- Backend `vandoxd` `v0.2.0-rc.1` (image `networlddev/vandox:0.2.0-rc.1`) in Docker on the Synology DS918+ (Celeron J3455),
  data in `/volume2/DockerData/Vandox/data` on a volume of two M.2 SSDs used as a normal volume (not a cache), reached over
  Tailscale. Container memory limit 512 MiB.
- Run dates: the agent readings carry their own times. The dates of the benchmark runs (sections 4 to 6) were not handed
  over; tables use 2026-11-09, the hand-over date, unless the orchestrator supplies the run dates.

## 1. Footprint while sending — process 117391

- `ActiveEnterTimestamp` 2026-11-07 21:29:57 CET, `NRestarts=0`.
- Reading 2026-11-09 09:20:00 CET, uptime 129,003 s (35 h 50 min 3 s): `CPUUsageNSec` 626,511,432,000; `VmHWM` 28,164 kB
  (unchanged since the first backfill); `VmRSS` 15,308 kB; `smaps_rollup` Rss 17,024 kB, Pss 17,020 kB (Pss_Anon 11,796 kB,
  Pss_File 5,224 kB); 10 threads; spool 13,256,510 B (`du`), 3 files.
- I/O at 09:20: `rchar` 4,798,456,748, `wchar` 354,326,516, `read_bytes` 398,999,552, `write_bytes` 515,022,848,
  `cancelled_write_bytes` 0.
- Reading 2026-11-07 21:39:15 CET (uptime 558 s): `CPUUsageNSec` 47,517,032,000, `VmHWM` 28,164 kB, `VmRSS` 18,148 kB; `wchar`
  48.1 MB.
- Reading 2026-11-08 10:57:39 CET (uptime 48,462 s): `CPUUsageNSec` 258,403,348,000, `VmHWM` 28,164 kB, `VmRSS` 13,096 kB.
- *(derived)* Mean CPU over the whole run, first backfill included: 0.49 % of one core (626.51 s / 129,003 s). In live sending,
  without the first 558 s: 0.45 % ((626.51 − 47.52) s / (129,003 − 558) s). At 48,462 s: 0.53 %.
- *(derived)* Peak RSS 28,164 kB = 28.8 MB (kB × 1,024 bytes, in MB of 10^6 bytes, as the existing *Agent footprint* section converts 19,896 kB to 20.4 MB) against
  30 MB: met, with 1.2 MB margin. The run without sending (#44) had 20.4 MB.

## 2. First backfill after the server started sending, 2026-11-07

- The spool held about 1.5 GB (about 90 data files of 16.77 MB, 2026-10-28 to 2026-11-07), accumulated while backend v0.1.0
  had no ingest API.
- Backend stored the first batch at 21:14:59 CET, sent by the previous agent process 96876 (a pre-release build).
- `v0.2.0-rc.1` installed 21:28:33; process 117228: backfill started 21:28:37, stopped by a manual restart at 21:29:55, after
  82 s and 11.620 s CPU.
- Process 117391: journal `backfill started` 21:30:01, `backfill finished` 21:35:34 with `records=1224755`.
- The backlog was sent over these three processes in about 20.5 minutes; the spool fell to 15 MB, 3 files.
- Backend: total received 178 MB by 21:39; 70 % CPU and 322 MiB of 512 MiB right after the backfill; 185.8 MiB and 0.05 % CPU
  at 10:57 on 2026-11-08.
- *(derived)* 333 s for 1,224,755 records = 3,678 records/s (process 117391's share only). Backend received about 141 KiB/s on
  average over the 20.5 minutes.
- *(derived, rough)* CPU of process 117391 in the backfill about 46 s: about 14 % of one core for 333 s, 38 µs per record.
  Process 117228: 11.62 s CPU over 82 s = 14.2 %.
- *(derived)* The 256 KiB/s limit was not binding: `wchar` was 48.1 MB for the whole first 558 s of process 117391.

## 3. Clean backfill (the real rate), 2026-11-09 — process 300777

- Process started 09:24:37 CET (fresh `VmHWM`). `tailscaled` stopped 09:32:44, started 16:13:06; the agent kept spooling.
- Journal: warning `backend not accepting batches` from 09:33:15; 16:13:35 `backend accepting batches again` `seconds=24019`
  (24,019 s = 6 h 40 min); 16:13:35 `backfill started`; 16:14:11 `backfill finished` `records=126835`.
- Readings:

| Time (CET) | `CPUUsageNSec` | `VmHWM` (kB) | `VmRSS` (kB) | Spool (B / files) | `wchar` |
| ---------- | -------------: | -----------: | -----------: | ----------------- | ------: |
| 09:32:33 (uptime 476 s) | 3,235,278,000 | 14,888 | 14,028 | 14,658,477 / 3 | 1,177,485 |
| 16:11:57 | 103,253,278,000 | 19,188 | 16,104 | 58,251,759 / 6 | 50,639,190 |
| 16:13:51 (16 s into the backfill) | 105,797,105,000 | 26,796 | 24,760 | 24,994,522 / 4 | 53,295,548 |
| 16:14:14 | 108,054,525,000 | 28,168 (28.8 MB) | 18,052 | 8,256,715 / 3 | 55,895,822 |

- *(derived)* 36 s for 126,835 records = 3,523 records/s; a day's backlog of 544,560 records about 2 min 35 s at that rate
  (`docs/areas/agent-sending.md` computed about 3 minutes at 3,207/s).
- *(derived)* CPU during the outage (09:32:33 to 16:11:57, 23,964 s): 100.02 s = 0.42 % of one core.
- *(derived)* Spool growth 43.59 MB in 23,964 s = 6.55 MB/h = 157 MB a day (the run of #44 also gave 157 MB a day).
- *(derived)* 16:11:57 to 16:14:14 (137 s): 4.80 s CPU; the backfill itself about 12 % of one core (rough: 4.80 s minus the
  0.42 % baseline over the 101 s outside the backfill, over 36 s), about 34 µs per record.
- *(derived)* Bytes written in those 137 s at most 5.26 MB (`wchar`, which includes every write of the process), about
  41 bytes per record, about 143 KiB/s over the 36 s: the 256 KiB/s limit was not binding in either backfill.
- The peak 28,168 kB (28.8 MB) is the same as in the first backfill.
- Backend (`docker stats`, 30 s sampling): 16:12:47 0.05 % 231.7 MiB / 512 MiB; 16:13:24 0.04 % 231.7 MiB; 16:13:56 59.25 %
  269.3 MiB.
- Backend memory when idle: 185.8 MiB (2026-11-08 10:57), 199.4 MiB (2026-11-09 about 09:20, 2.79 % CPU), 231.7 MiB (2026-11-09
  16:12). The Product Manager considers this not too much: an **observation**, not a finding.

## 4. Memory pressure and the process snapshot on the server

- Journal of process 117391: 2026-11-08 00:36:27 `memory pressure mode on` (`mem_available_bytes` 1,016,365,056,
  `memory_some_avg10` 11.44); 00:36:37 `memory pressure mode off` (`mem_available_bytes` 1,018,392,576, `memory_some_avg10`
  4.21): one episode of 10 s. It shows that an episode occurs and ends by itself; its CPU cost cannot be separated from the
  total. `VmHWM` of the process was 28,164 kB before (21:39:15) and after (09:20) the episode.
- `BenchmarkProcesses_Collect` (pressure mode on, every process read incl. `smaps_rollup`) on the server as root, test binary
  built from `3e9611b`, 174 processes (no sleepers started, so not the 250 of the criterion), `-test.count 10 -test.benchtime
  5x`: 61.87, 67.29, 53.29, 54.58, 58.92, 55.02, 56.01, 54.07, 53.84, 56.07 ms; median 55.5 ms (53.3 to 67.3).
- *(derived)* One such snapshot every 15 s under pressure = 0.37 % of one core, in wall time.

## 5. Spool open on the server

`spool.test` built from `3e9611b`, linux/amd64, run on the server, `-test.benchtime 3x`, CPU "AMD EPYC Processor (with IBPB)":
`BenchmarkOpen_Full` (about 1.94 GiB in a cap of 2 GiB) 55,093,710 ns/op = 55.1 ms; `BenchmarkOpen_ManyFiles` (1,024 files)
26,079,326 ns/op = 26.1 ms; both against the 5 s bound (development host 26.2 ms and 15.9 ms).

## 6. Storage write criterion on the reference host

- DS918+, the SSD volume of the setup; flush of a 4 KiB write median 9.0 to 10.0 ms.
- Document's procedure: `tools/Vandox.StorageBenchmark` published (`dotnet publish -c Release`, framework-dependent, from main
  `3e9611b`), run in the `mcr.microsoft.com/dotnet/runtime:10.0` container (Ubuntu 24.04.5 inside, .NET 10.0.14, 4 logical
  CPUs) with the folder on the volume. Three runs: medians of five batches 936, 934 and 906 ms (criterion under 1000 ms met in
  all three); batches 2 to 5 between 852 and 974 ms, about 10,300 to 11,700 records/s; first batch 1,438 to 1,580 ms. One run
  with `--warmup`: warm-up batch of 1000 records 368 ms, batches 1331 / 856 / 872 / 897 / 907, median 897 ms, met.
- Before: 783 ms median (row of 2026-10-07, before the agent timeline); the document predicted about 900 ms with the timeline;
  measured 897 to 936 ms.
- A different, slower procedure (not the document's): `dotnet run` inside `mcr.microsoft.com/dotnet/sdk:10.0` gave medians
  1112, 964 and 1077 ms (met once of three; first batch 1.6 to 2.0 s).
- `--backfill`, only with that SDK-container procedure, five rounds each: medians of the filling batch 1603, 1487 and 1561 ms
  (rounds 1378 to 2548 ms; first rounds 2010, 2548, 2298 ms). Not repeated with the published program.

## 7. `--backfill` with the published program (added later)

On 2026-11-09 the Product Manager ran the `--backfill` scenario once with the published program (framework-dependent publish of main
`3e9611b`, `mcr.microsoft.com/dotnet/runtime:10.0` container on the DS918+ SSD volume, folder `/bench/bf4`), five rounds: 1804, 1482, 1366,
1338, 1369 ms (first round 1804, fastest 1338, median 1369, slowest 1804 ms; 5544, 6746, 7319, 7474, 7302 records/s). A single run; the
scenario has no criterion of its own.

## Build of the test binaries

`procstats.test` and `spool.test` were built with Go 1.27.0 in the orchestrator's sandbox (linux/amd64, CGO off) from commit `3e9611b`.
