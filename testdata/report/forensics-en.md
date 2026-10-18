# Vandox forensics report

- Generated: 2026-10-06 07:00 UTC
- Log lines: 123456 (first 2026-09-29 00:00 UTC, last 2026-10-06 06:00 UTC)
- Observed hours: 168
- Process snapshots: 2016 (first 2026-09-29 00:05 UTC, last 2026-10-06 05:55 UTC)
- Lookback in minutes: 60
- All times are UTC.

## Summary

- Outages: 13, OOM kills: 29
- First outage: 2026-09-30 01:05 UTC
- Last outage: 2026-10-05 22:40 UTC
- Time of day 02:00–03:00 UTC: 3 of 13 outages (23.1 %) against 4.2 % of the observed hours
- Backup running: 3 of 3 outages (100.0 %) against 4.2 % of the observed hours
- Rules without a result: Boot (too many boots), Panel memory high (no panel memory)

## Recurring patterns

### Time of day (UTC)

| Hour | Outages | Share of outages | Share of observed hours | Result |
| --- | ---: | ---: | ---: | --- |
| 02:00–03:00 | 3 | 23.1 % | 4.2 % | detected |
| 05:00–06:00 | 1 | 7.7 % | 4.2 % | not detected |

### Activities before the outages

| Activity | Outages with it | Outages known | Share of outages | Share of observed hours | Result | Reason |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| Backup running | 3 | 3 | 100.0 % | 4.2 % | detected | — |
| Boot | 0 | 0 | — | — | unknown | too many boots |
| Panel memory high | 0 | 0 | — | — | unknown | no panel memory |

Panel memory threshold: 68 MiB

## Memory consumers

### Permanent consumers

- ` mariadbd `: in 100.0 % of the snapshots, mean 300 MiB, peak 400 MiB

### Transient consumers

- ` pzstd `: in 0.6 % of the snapshots, peak 150 MiB at 2026-10-02 06:59 UTC

## New listeners

- Agent ` web-1 `: 1440 connection snapshots from 2026-09-29 00:05 UTC to 2026-10-06 05:55 UTC; 4 agent starts: 2 compared on all ports, 1 on ports 1 to 1023 only, 1 not compared
- Agent ` web-2 `: 12 connection snapshots from 2026-10-03 08:00 UTC to 2026-10-03 09:50 UTC; 0 agent starts: 0 compared on all ports, 0 on ports 1 to 1023 only, 0 not compared
- 2026-10-02 06:40 UTC, agent ` web-1 `, after an agent start (compared with 2026-10-02 06:10 UTC), all ports: 2 new
  - tcp 0.0.0.0:23, PID 200 ` telnetd `
  - tcp6 [::]:8443, no owner
- 2026-10-04 03:12 UTC, agent ` web-1 `, after an agent start (compared with 2026-10-04 02:42 UTC), ports 1 to 1023 only: 7 new
  - tcp 0.0.0.0:25, PID 311 ` exim4 `
  - and 6 more
- 2026-10-05 21:00 UTC, agent ` web-1 `, after an agent start, not compared: ports 1 to 1023 not complete
- 2026-10-05 12:00 UTC, agent ` web-1 `, found by the agent, all ports: 1 new
  - tcp 0.0.0.0:2121, no owner
- 2026-10-06 04:00 UTC, agent ` web-2 `, found by the agent, ports 1 to 1023 only: 1 new
  - udp 0.0.0.0:69, PID 12 `` tftp`d ``

## Outages

### Outage #7 at 2026-10-02 06:59 UTC

- Window: 2026-10-02 05:59 UTC – 2026-10-02 07:29 UTC
- Kills: 5
- Victims: ` mariadbd ` (2), ` php-fpm ` (1), unknown (1)
- Kills not kept: 1
- Growth candidates: ` pzstd ` +768 MiB, ` php-fpm ` +2 MiB
- Largest consumers: ` apache2 ` 400 MiB, ` mariadbd ` 300 MiB
- Activities before the outage:
  - Backup running: yes
  - Boot: yes (last boot 2026-10-02 06:30 UTC)
  - Panel memory high: no
- Service events in the lookback: 2

### Outage #8 at 2026-10-03 06:59 UTC

- Window: 2026-10-03 05:59 UTC – 2026-10-03 07:29 UTC
- Kills: 1
- Victims: ` nginx ` (1)
- Growth candidates: unknown
- Largest consumers: unknown
- Activities before the outage:
  - Backup running: no
  - Boot: no
  - Panel memory high: unknown
- Service events in the lookback: 0

### Outage #9 at 2026-10-04 06:59 UTC

- Window: 2026-10-04 05:59 UTC – 2026-10-04 07:29 UTC
- Kills: 1
- Victims: ` cron ` (1)
- Growth candidates: none
- Largest consumers: none
- Activities before the outage:
  - Backup running: unknown
  - Boot: unknown
  - Panel memory high: yes
- Service events in the lookback: 0

## Notes

- A pattern is reported when at least 2 of at least 3 known outages share a property, their share is above the share of the observed hours and the probability of that many by chance is at most 1 % (1 % / 24 for one hour of the day). Observed hours are UTC hours with a line of the system log (journal or syslog); an activity is judged over the lookback before the first kill. A rule that cannot be decided is shown as unknown with its reason, never as not detected.
- The report keeps at most 1000000 process snapshot points; the later snapshots were not used, so backup and panel memory are unknown.
- The report keeps at most 10000 boots; the later boots were not used, so the boot rule is unknown.
- The report keeps at most 100000 runs of consecutive observed hours; the later hours were not used, so backup, boot and panel memory are unknown.
- 7 program entries were not tracked because the report tracks at most 4096 programs.
- The number of incidents changed between the two passes over the stored incidents, so the rules and the list of outages may not match; run vandoxd incidents and the report again.
