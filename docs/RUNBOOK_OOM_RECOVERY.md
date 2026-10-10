# Runbook: recover the server after an out-of-memory outage

The monitored server (Ubuntu 22.04, Plesk 18, about 2 GB RAM) can run out of memory under load. The kernel
OOM killer then ends `mariadbd`, and Plesk and mail go down with it
([0025](decisions/0025-server-ram-stays-at-2-gb.md)). This runbook restores service **without causing the
next OOM kill**. Two earlier recoveries went wrong in exactly that way:

- 30 Sep 2026: services restarted by hand while memory was still full were killed again (MariaDB at 19:45,
  and at 20:06 after its restart).
- 2 Oct 2026: after a reboot at 06:51 Plesk started the backup that had failed during the night while all
  services were starting; MariaDB was OOM-killed at 06:59.

The cause both times was too many processes at once, with MariaDB as the victim. The runbook is also the
template for the self-healing rules of release v0.6.0: start order, memory check between steps and the stop
condition are what those rules encode.

## When to use it

- `mariadb` is not running, or the Plesk panel (port 8443), websites or mail do not answer.
- The kernel log shows `Out of memory` or `Killed process` in the last hours.
- The server was rebooted after such an outage, and services are still starting.

## Rules

1. **Free memory before starting anything.** A service started into full memory is killed again.
2. **Start services one at a time, in the fixed order of step 2,** and check available memory after each
   one. Never `systemctl restart` everything at once.
3. **Stop when the memory check fails.** Do not push the next service in; find the consumer (step 1).
4. **Do not reboot as a first reaction.** If a reboot is unavoidable, do the backup hold of step 1 first;
   it survives the reboot.

## Thresholds

| Value | Limit to continue | Basis |
|---|---|---|
| `MemAvailable` | at least 400 MiB | normal operation 950–1225 MiB (2026-10-04, 2026-10-10); lowest value in a backup night 266 MiB (night to 2026-10-03) |
| Memory PSI `some avg10` | below 10 | `/proc/pressure/memory`; pressure shows before the OOM killer acts |
| Wait per service | up to 300 s | then stop (rule 3) |

## Step 0: assess (read-only)

```bash
free -m
cat /proc/pressure/memory
swapon --show
grep -E 'oom_kill|pswpin|pswpout' /proc/vmstat
journalctl -k -S -2h -g 'Out of memory|Killed process' --no-pager | tail -n 20
systemctl --failed --no-legend
```

Check that swap is active. The swap file is `/swapfile` and has an entry in `/etc/fstab`, so it comes back
after a reboot; if `swapon --show` is empty anyway, run `swapon -a`. The `oom_kill` counter shows how many
kills happened since boot.

## Step 1: free memory first

Plesk's backup manager `backupmng` is started by cron every 15 minutes
(`/etc/cron.d/plesk-backup-manager-task`) and catches up missed backups, which is what happened on 2 Oct.
Move the cron file aside so no backup starts while you work; step 3 moves it back:

```bash
mv /etc/cron.d/plesk-backup-manager-task /root/plesk-backup-manager-task.held
```

List the largest consumers and what is running right now:

```bash
ps -eo pid,ppid,user,rss,etime,args --sort=-rss | head -n 15
pgrep -a -f 'pzstd|pmm' || echo "no backup process running"
```

`rss` is in KiB. A running Plesk backup (`pzstd`, about 200 MiB) is the usual cause: stop it with
`pkill -TERM pzstd`. Plesk marks that backup as failed and it runs again later.

Check again with `free -m` and `cat /proc/pressure/memory`. Continue only when both are inside the limits of
the table above. If memory stays low with no backup running, the consumer is something else in the `ps`
list: note it (step 5) and stop that service reversibly (`systemctl stop`), never kill `mariadbd`.

## Step 2: start services one at a time

Order (each unit is started on its own, a memory check follows each one):

| # | Unit | Why here |
|---|---|---|
| 1 | `mariadb` | everything else depends on it |
| 2 | `sw-cp-server`, `sw-engine`, `psa` | Plesk panel and core |
| 3 | `named`, `xinetd` | light services (DNS, FTP/poppassword) |
| 4 | `plesk-php85-fpm`, `apache2`, `nginx` | web stack, nginx in front of Apache |
| 5 | `postfix`, `dovecot` | mail transport and mailboxes |
| 6 | `fail2ban` | protection |
| 7 | `amavis`, `spamassassin`, `pc-remote` | mail filters last: they are large (about 490 MiB together) |

All 15 units exist on the server (`systemctl cat`, checked 2026-10-10). While the filters are down, Postfix
defers mail; it is not lost.

Paste both functions into the root shell, then run `vandox_recover`. Units that are already running or do
not exist are skipped; it stops at the first failed start or memory check, prints what is left and returns
without closing the shell.

```bash
# mem_gate [MIN_AVAILABLE_MIB] [MAX_WAIT_SECONDS]
mem_gate() {
  local min=${1:-400} max=${2:-300} waited=0 avail psi
  while :; do
    avail=$(awk '/^MemAvailable:/ {print int($2/1024)}' /proc/meminfo)
    psi=$(awk '/^some/ {split($2, a, "="); print a[2]}' /proc/pressure/memory 2>/dev/null)
    psi=${psi:-0}
    if [ "$avail" -ge "$min" ] && [ "${psi%.*}" -lt 10 ]; then
      echo "ok: MemAvailable=${avail} MiB, PSI some avg10=${psi}"
      return 0
    fi
    if [ "$waited" -ge "$max" ]; then
      echo "STOP: MemAvailable=${avail} MiB, PSI some avg10=${psi} after ${waited}s" >&2
      return 1
    fi
    sleep 10
    waited=$((waited + 10))
  done
}

units=(mariadb sw-cp-server sw-engine psa named xinetd plesk-php85-fpm apache2 nginx
       postfix dovecot fail2ban amavis spamassassin pc-remote)

vandox_recover() {
  mem_gate 400 300 || return 1               # nothing starts before memory is free
  local i u
  for i in "${!units[@]}"; do
    u=${units[$i]}
    systemctl cat "$u" >/dev/null 2>&1 || { echo "skip (no such unit): $u"; continue; }
    systemctl is-active --quiet "$u" && { echo "already active: $u"; continue; }
    echo "starting $u"
    systemctl start "$u" || { echo "FAILED to start $u; left: ${units[*]:$i}" >&2; return 1; }
    sleep 20
    mem_gate 400 300 || { echo "left (not started): ${units[*]:$((i + 1))}" >&2; return 1; }
  done
  echo "all units processed"
}

vandox_recover
```

If it stops, go back to step 1. Start the remaining units later in the same order; do not skip ahead.

## Step 3: release the backup manager

When step 4 passed, the system has been stable for at least 15 minutes and `mem_gate 600 60` passes, move
the cron file back. The missed backup then runs at the next cron tick as a normal backup:

```bash
mv /root/plesk-backup-manager-task.held /etc/cron.d/plesk-backup-manager-task
```

Do not forget this: while the file is held, no scheduled backup runs at all.

## Step 4: verify

```bash
systemctl is-active mariadb sw-cp-server sw-engine psa apache2 nginx postfix dovecot amavis spamassassin
plesk db "SELECT 1"
curl -sk -o /dev/null -w '%{http_code}\n' https://127.0.0.1:8443/    # 200 or 302
postqueue -p | tail -n 1
ss -ltn | grep -E ':(25|80|443|465|587|993|8443)\b'
free -m; cat /proc/pressure/memory
```

Done when every unit says `active`, MariaDB answers, the panel responds, the mail queue is shrinking and the
memory values are inside the limits. Watch for another ten minutes; memory pressure often returns when the
mail filters start working through the backlog.

## Step 5: record what happened

Vandox imports logs and explains outages afterwards, so keep the evidence:

```bash
journalctl -k -S -6h > /var/tmp/oom-$(date +%F-%H%M).kernel.log
grep -E 'oom_kill|pswpin|pswpout' /proc/vmstat >> /var/tmp/oom-$(date +%F-%H%M).kernel.log
ls -l /etc/cron.d/plesk-backup-manager-task   # must exist; if not, do step 3
```

Note the time of the first kill, the victim, what was running (backup?), and the time each step finished. If
a step needed a change to this runbook, change the runbook.

## Limits

- This runbook does not change memory settings, `OOMScoreAdjust` or services; those belong to the tuning
  work and the service inventory ([0026](decisions/0026-services-disabled-reversibly-only.md)).
- Hosting-provider agents (`nydus-ex`, `nydus-ex-api`, `qemu-guest-agent`) are never touched.
