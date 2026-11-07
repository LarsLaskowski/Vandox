# 0165: Error logs the agent cannot read get a documented grant by hand, not a new right

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent rights
- **Source:** Issue #289
- **Supersedes:** —

## Context

The Plesk panel log and the web servers' error logs (#289, record 0164) are not readable through the agent's groups on a usual
server. Record 0122 dropped `CAP_DAC_READ_SEARCH`, because together with `CAP_SYS_PTRACE` it lets the agent write into other
processes' open files. A rotation that creates a new file sets its mode and owner anew, and Plesk may repair file rights.

## Options considered

1. **`CAP_DAC_READ_SEARCH`** — ruled out by record 0122.
2. **An ACL that the installer sets** — a new right of the agent, a `security` change.
3. **No new right, and a grant by hand that `deploy/agent/README.md` documents, with a default ACL on the directory so that a file a
   rotation creates inherits it** (chosen by the Product Manager) — the agent keeps its rights; a file the group `adm` cannot read is
   not shipped until the operator grants read access.

## Decision

Option 3: the agent gains no right for these files; one its user and groups cannot read is logged and not shipped, and the README
documents the grant by hand. The procedure is in `deploy/agent/README.md` (*Error logs of Plesk and the web servers*), what the agent
does with an unreadable file in [Agent rights](../areas/agent-rights.md) and [Agent log shipping](../areas/agent-log-shipping.md).

## Consequences

- Measured in the plan review: plain `setfacl` follows a link planted by a directory's owner and would grant the agent its target, so
  the procedure checks the path with `namei -l` first and uses `setfacl -P`; a default ACL replaces the umask for every later file and
  subdirectory, so it is recommended only where rotation creates files with an explicit mode.
- How the grant survives rotation was measured on a stand-in (no Plesk server, no container daemon); the README states each result as
  measured there and marks its statements about the server's own file rights, rotation and `plesk repair fs` unverified.
- The grant widens what a compromised agent can read.
- A grant can be lost by a rotation with a restrictive mode or a Plesk repair; the standing `log file not readable` warning is the
  sign, and a renamed file's rest waits until the path opens again (a path that cannot be opened is not a failing read, so record
  0169's bound does not count it).
