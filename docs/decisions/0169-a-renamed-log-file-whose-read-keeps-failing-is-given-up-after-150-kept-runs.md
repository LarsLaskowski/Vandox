# 0169: A renamed log file whose read keeps failing is given up after 150 kept runs

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #291
- **Supersedes:** —

## Context

After a rotation the `logs` collector finishes the renamed file before it reads the newer file at the path, so that a file's lines
keep their order (record 0162). #291 found that a renamed file whose reads keep failing held back the newer lines of its configured
file without end; a restart does not help, because the renamed file is found again by its identity. A gap must never start after the
lines it loses (record 0166).

## Options considered

1. **No bound** — no readable line is ever lost, but the newer lines of the file never alert.
2. **A bound by the wall clock** — a clock step would shorten or stretch it, and the run's start is already distrusted for line times.
3. **150 kept runs in a row whose read of the renamed file fails without moving its offset, about 5 minutes at the 2-second interval**
   (chosen) — longer than the three runs of a damaged journal file, because a damaged structure stays damaged while an I/O error can
   pass; the count lives in the reading state a run works on, so a dropped run undoes it.
4. **Skip only the unreadable part and read on behind it** — an I/O error has no known extent; **give up the whole rest with one gap**
   (chosen), as at a truncation.
5. **Start the gap at the file's last time** — lines of the file at the path move it while the renamed file is still read, so the
   gap could start after lines it loses. **The renamed part's own last time, kept in memory** (chosen); saving it in the state file
   would cover a restart too, but adds a key to a strictly decoded file for a case that needs a restart during a failing read.

## Decision

A renamed file whose read fails in 150 kept runs in a row is given up with one gap from its own last time, and the newer file is read
on in the same run. The rules are in [Agent log shipping](../areas/agent-log-shipping.md) (*Rotation*).

## Consequences

- The give-up trades lines that may still be readable later for the live lines of the newer file.
- The count is not saved, so an agent restarted within every 150 runs never gives up, and after a restart the gap starts at the saved
  file's last time, which can lie after lines it loses (a residual the area states).
- A path that cannot be opened is not a failing read, so a lost read grant (record 0165) does not count toward the bound.
