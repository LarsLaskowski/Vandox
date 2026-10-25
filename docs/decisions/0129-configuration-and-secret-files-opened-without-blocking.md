# 0129: Configuration and secret files are opened without blocking and their type is checked on the opened file

- **Status:** Accepted
- **Date:** 2026-10-25
- **Area:** Configuration and secrets
- **Source:** Issue #263 / PR #264
- **Supersedes:** 0049 (amends it: the configuration file and a `_FILE` secret file are opened without blocking and their type is checked on the opened descriptor, not on the path before the open; 0049 is not edited)

## Context

Record 0049 requires the configuration file to be a regular file, so that a FIFO or a device named as the file is
refused instead of read. The agent checked the type of the path first and opened the file afterwards, for the
configuration file and for the `_FILE` secret files (0050) alike. Issue #263 showed the window between the two steps: a
FIFO swapped in after the check is opened anyway, and opening a FIFO without a writer blocks start-up. The backend
already opened both through `FileProbe.OpenRegular`, but its documentation still described the check before the open.
Only root can replace a file in `/etc/vandox`, but a start-up that hangs without a message is hard to diagnose.

## Options considered

1. **Keep the type check on the path before the open.** It needs no change, but leaves the window of issue #263 open.
2. **Open without blocking (`O_NONBLOCK`, and `O_NOCTTY` in the agent) and check the type on the opened descriptor.**
   A FIFO or device swapped in at any moment is opened but refused before a byte is read, and nothing blocks.
   A device is still opened once before it is refused (in the agent also one named as the file, as it checks no path
   first), which for the files Vandox reads has no effect.
3. **Open with `O_PATH`, check the type, and open again through `/proc/self/fd`.** It avoids opening a swapped-in
   device at all, but needs `/proc` and two opens for a race only root can trigger in `/etc/vandox`.

## Decision

Option 2: both binaries open the configuration file and a `_FILE` secret file without blocking and check on the opened
descriptor that it is a regular file (the agent in `internal/config`, the backend through `FileProbe.OpenRegular`).
The backend still checks the path before the open, which only gives a missing or non-regular path its own message; the
agent checks the opened descriptor only. The resulting behavior is in
[Configuration and secrets](../areas/configuration-and-secrets.md), *Configuration file* and *Secrets*.

## Consequences

- A FIFO or device named as the file, or swapped in while it is opened, is refused without being read and cannot block
  start-up.
- A device is opened once before it is refused: in the agent any device named as the file, in the backend one swapped
  in after the path check. Should a device whose open has side effects ever become a concern, option 3 is the way to
  revisit this record.
