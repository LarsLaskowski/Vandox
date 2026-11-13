# Retire the legacy monitoring script

This guide is for the operator whose monitored server still runs the shell script that monitored it before the agent existed:
`/usr/local/bin/syslog.sh`, started every minute by root's crontab, writing `/var/log/sysmonitor.log`. It shows how to judge the
agent against the script, how to keep the script's history in Vandox and how to take the script out of root's crontab. It starts
when the agent from [step 11 of the installation guide](INSTALL.md#11-install-the-agent-on-the-monitored-server) already runs beside
the script. Whether the agent is good enough, and when to retire, is your decision; this guide does not state the result of any
parallel run.

Server commands are for a root shell (`sudo -i`), **one line at a time**. Commands for the backend host are marked as such and are
the ones of [steps 6 and 7 of the installation guide](INSTALL.md#6-collect-the-logs-on-the-monitored-server) with the archive's
name, run where step 1 of that guide put `$VANDOX_USER` and the `import/` directory. The backend needs `import.time_zone` for this
log (step 2 of the installation guide).

## Compare first

Let both run side by side and look at the same time window in the web UI: the agent's data in the incident pages and the *Logs*
view, the script's in `sysmonitor.log` on the server. The agent reads `/proc` directly and starts no `top` or `lsof`, so the numbers
are of the same kind, not the same bytes. Each line of the table is one kind of data the script writes; the last column names where
the area documents state the differences.

| The script writes | The agent records instead | How often | Where you see it | Differences |
| ----------------- | ------------------------- | --------- | ---------------- | ----------- |
| the `top` summary: load average, CPU, memory and swap lines | the `system` collector: `cpu_percent` per mode, `load1`, `load5`, `load15` and the memory and swap values as `top` prints them (procps 3.3.17) | every 30 s (10 s under memory pressure), the script every minute | the extremes of load, memory and swap on an incident page and in the report | a CPU rate has no value in the first sample after the agent starts; see [Agent system metrics](areas/agent-system-metrics.md) |
| the `top` process table | the `processes` collector: a process snapshot with the programs summed over every process and the top 25 by memory and by CPU listed (every process under memory pressure) | every 60 s (15 s under memory pressure) | the memory-per-program charts of an incident page | the agent ranks by PSS where it may read `smaps_rollup`, `top` by RSS, so the memory of a program differs; program names follow the script's derivation, but the forms of `php-fpm: pool ...`, `spamd` and `amavisd` were not captured from a Plesk server; see [Agent system metrics](areas/agent-system-metrics.md), *Processes* |
| the `meminfo` section | one record for every line of `/proc/meminfo` | every 30 s (10 s under memory pressure) | memory and swap extremes and charts | a field the kernel does not offer is absent, never 0; see [Agent system metrics](areas/agent-system-metrics.md) |
| the `pressure` section | the pressure values per resource (memory, CPU, I/O) and kind (`some`, `full`) | every 30 s (10 s under memory pressure) | the pressure extremes of an incident page | a kernel without pressure stall information gives no values and one warning in the agent's log; see [Agent system metrics](areas/agent-system-metrics.md) |
| the `vmstat` section | the counters `pswpin`, `pswpout`, `pgmajfault` and `oom_kill` and their rates per second | every 30 s (10 s under memory pressure) | the swap and fault figures of an incident page | the first sample after the agent starts has no rate; see [Agent system metrics](areas/agent-system-metrics.md) |
| the `lsof -ni` connections | the `connections` collector: listeners, states and connections from the kernel's socket tables, with the owning process where it can be read | every 60 s (15 s under memory pressure) | the connection charts of an incident page; new listeners in the report | the agent's totals include sockets without an owner, which `lsof -ni` never listed, so the numbers differ; see [Agent connections](areas/agent-connections.md) |

How the imported script log is read, and what it cannot show, is in the
[Legacy monitoring log parser](areas/log-parsers-legacy-monitoring.md). The agent's schedule and its memory pressure mode are in
[Agent](areas/agent.md).

**If an item is missing or worse than the script's, do not retire the script.** Keep it running, open an issue in the Vandox
repository with the item, the time window you compared and what the script and the agent each showed, and retire only after the
agent delivers it.

## What this guide changes and what it does not

- **Before the retirement the server changes in one way only:** one archive of `sysmonitor.log` and its rotated copies in your
  login's home, mode `0600`, which you delete once it is copied.
- **Do not rerun step 6 of the installation guide as a whole** for this. It archives all of `/var/log` and a fresh journal export.
  The agent has shipped what the server logged since it started, and an import does not deduplicate across sources or with live data
  ([Log import](areas/log-import.md)), so importing more than the script's log stores again what was shipped or imported before.
  Pack `sysmonitor.log*` and nothing else.
- **The parallel period is stored twice on purpose.** The script's log of the days the agent also ran is imported under the source
  `legacy-monitor` beside the agent's own data of the same days; the incident pages read both, and the agent's connection totals,
  being higher, usually win the connection peak.
- **Rotation does not wait for you.** `/etc/logrotate.d/sysmonitor` rotates the log daily at 00:00 and keeps 14 copies
  (`sysmonitor.log.1.gz` to `.14.gz`). It keeps deleting copies older than about 14 days, also after the script has stopped. That is
  why step 1 copies every file first, and why step 3 has to happen within those 14 days.
- **The script and its logs stay on the server** after the retirement, but they are not your kept copy. The kept copy is the two
  archives in the backend's `import/` directory (keep them, or copy them to your backup).

## 1. Copy and import the log

On the monitored server, in a root shell:

```
sudo -i
```

Read the home directory of your login (`sudo -i` makes `~` root's home, so it is read from the account) and pack the script's log
and its rotated copies. The glob is expanded in `/var/log`, only `sysmonitor.log*` is packed, and your login, not root, writes the
archive with mode `0600`; `${SUDO_USER:?}` and `${home:?}` stop a line whose variable was lost:

```
home=$(getent passwd "${SUDO_USER:?}" | cut -d: -f6) && [ -n "$home" ]
set -o pipefail && (cd /var/log && tar -czf - sysmonitor.log*) | runuser -u "${SUDO_USER:?}" -- sh -c 'umask 077 && cat > "$1"' sh "${home:?}/sysmonitor-before-retirement.tar.gz"
```

Copy it to the backend host and import it (on the backend host, as in steps 6 and 7 of the installation guide):

```
scp server:sysmonitor-before-retirement.tar.gz /volume1/docker/vandox/import/
sudo chown "$VANDOX_USER" /volume1/docker/vandox/import/sysmonitor-before-retirement.tar.gz
sudo chmod 0400 /volume1/docker/vandox/import/sysmonitor-before-retirement.tar.gz
sudo docker exec -it vandoxd dotnet /app/vandoxd.dll import /import/sysmonitor-before-retirement.tar.gz
```

Read the summary at the end of the import: each file must be listed as imported, or, where an earlier import (step 6 of the installation guide) already held the script's log, as continued, contained or already imported. If the import skips snapshots or does not recognize
the log (the format was built from a description, not from the real file), the import's report names each skipped snapshot with its
reason. Do not edit the log by hand; open an issue with the skipped shapes (the lines the report names, with the usernames and
addresses removed) and stop here. A failure with `import.time_zone is not set` is completed by setting the option and importing
again (step 7 of the installation guide).

When the archive is imported, delete it on the server (a block of its own, after the copy; it also removes the leftover of a pack
that failed):

```
rm -- "${home:?}/sysmonitor-before-retirement.tar.gz"
```

If the pack line failed, the archive may be incomplete: do not copy it, fix the cause, and delete it with the line above before you
try again. A failure with `tar: sysmonitor.log: file changed as we read it` only means the script wrote during the pack: delete the archive and run the pack line again.

## 2. Retire the script

Only when the comparison is satisfactory and the archive of step 1 is imported and kept. On the monitored server, in the root shell
of step 1, this one line saves root's crontab, removes only the exact line `* * * * * /usr/local/bin/syslog.sh` from a copy, and
installs the copy only if it is exactly one line shorter. The other entries, such as those of Plesk's `psaadm`, stay:

```
(set -C && umask 077 && crontab -l > /root/crontab.before-vandox && grep -vxF '* * * * * /usr/local/bin/syslog.sh' /root/crontab.before-vandox > /root/crontab.vandox-filtered && [ "$(grep -c '' /root/crontab.before-vandox)" -eq $(( $(grep -c '' /root/crontab.vandox-filtered) + 1 )) ] && crontab /root/crontab.vandox-filtered)
```

- It never overwrites its two files (`/root/crontab.before-vandox`, the crontab of before, and `/root/crontab.vandox-filtered`), so a
  second run fails before it installs anything, and the undo below stays possible. If a run stops with `cannot overwrite existing
  file`, read both files, remove them only if they are a leftover of an attempt that installed nothing, and run the line again.
- If the line prints nothing and installs nothing, the crontab does not hold the exact line (it may differ, for example by a
  redirect), holds it twice, or holds nothing else. A crontab that holds nothing but the script line leaves nothing to install: remove
  the line with `crontab -e` then. Check what the crontab holds with `crontab -l`:

```
crontab -l
```

Cron may have started the current minute's run just before the change, so wait until the log stops growing: the size and time must be
the same in both readings, two minutes apart (a smaller size means the daily rotation ran; read again):

```
stat -c '%s %y' /var/log/sysmonitor.log; sleep 120; stat -c '%s %y' /var/log/sysmonitor.log
```

## 3. Copy and import the log again

Do this within 14 days of step 1, once the wait of step 2 is over: rotation deletes the copies that hold the snapshots written since
step 1 (they are in `sysmonitor.log` and, after a midnight, in `sysmonitor.log.1.gz` and the following copies) after about 14 days.
Pack the log and all its rotated copies again, under another name:

```
home=$(getent passwd "${SUDO_USER:?}" | cut -d: -f6) && [ -n "$home" ]
set -o pipefail && (cd /var/log && tar -czf - sysmonitor.log*) | runuser -u "${SUDO_USER:?}" -- sh -c 'umask 077 && cat > "$1"' sh "${home:?}/sysmonitor-after-retirement.tar.gz"
```

On the backend host:

```
scp server:sysmonitor-after-retirement.tar.gz /volume1/docker/vandox/import/
sudo chown "$VANDOX_USER" /volume1/docker/vandox/import/sysmonitor-after-retirement.tar.gz
sudo chmod 0400 /volume1/docker/vandox/import/sysmonitor-after-retirement.tar.gz
sudo docker exec -it vandoxd dotnet /app/vandoxd.dll import /import/sysmonitor-after-retirement.tar.gz
```

On the server, after the copy:

```
rm -- "${home:?}/sysmonitor-after-retirement.tar.gz"
```

The import reports each file as imported, continued, contained or already imported: `sysmonitor.log` as it grew since step 1 is
continued after the part imported before, a rotated copy that step 1 imported is already imported, and one that holds only
snapshots of a file imported before is contained ([Log import](areas/log-import.md), *A file that grew is continued*). The snapshots
after the copy of step 1 up to the script's last run are stored, with or without a midnight rotation in between. Two things are
lost, and nothing can restore them:

- **The rest of the snapshot the copy of step 1 cut.** A copy of a growing file ends anywhere; that snapshot is stored as incomplete,
  and its remaining lines, which the continuation skips as lines before the first snapshot, are not read.
- **The `vmstat_per_second` rate of the first snapshot of a continuation.** A rate needs the snapshot before it, and a continued
  import starts without one; the raw `vmstat_total` of that snapshot is stored.

Both are described in the [Legacy monitoring log parser](areas/log-parsers-legacy-monitoring.md) (*The end of the input*, *Sections
(new format)*). As in step 1, a skipped snapshot or an unrecognized file goes into an issue with the report's skipped shapes, never
into an edit of the log.

## Undo

To put the script back, restore root's crontab from the file step 2 saved. This one line installs it only if the crontab is still
exactly the one step 2 installed, and then removes both saved files:

```
cmp -s <(crontab -l) /root/crontab.vandox-filtered && crontab /root/crontab.before-vandox && rm -- /root/crontab.before-vandox /root/crontab.vandox-filtered
```

If it installs nothing, the crontab changed since step 2: add the line `* * * * * /usr/local/bin/syslog.sh` with `crontab -e` and
leave the saved files for reading. The script file itself was never touched, so it runs again at the next minute. A script that
ran again writes to the same `sysmonitor.log`, and the next import continues it.
