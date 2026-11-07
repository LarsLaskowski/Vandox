# 0149: The report's New listeners section caps each kind of finding separately and lists changed endpoints without the command

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Listener changes
- **Source:** Issue #278, Issue #304 (the socket UID and user of each listener), Issue #305 (changed socket UIDs)
- **Supersedes:** —

## Context

The forensics report shows the findings of the comparison across agent starts
([0116](0116-listeners-across-agent-starts-compared-when-stored.md)) together with the agent's own new-listener findings in a
section *New listeners*. A server with passive FTP produces an agent finding about once a minute
([0143](0143-new-listener-check-against-the-last-inventory-bounded-privileged-ports-first.md)), and an endpoint shared by two users
that keeps toggling makes a change-only finding every few minutes (#305). The findings hold external text (commands of up to 1,024
bytes, user names up to 1,024 bytes on the wire) and the report page keeps the report's document per circuit, where the cost of a
text is set by its runs, worst when plain and escaped characters alternate (measured in the plan review of #304: about 22.9 KiB for a
command of 1,024 bytes).

## Options considered

1. **One cap for all findings** (100, then the latest) — simpler, but agent findings of passive FTP would push the restart findings
   out within hours (spec challenge). Rejected for separate caps per kind.
2. **Lower caps per kind (7 to 10, to stay near 5.5 MB on the page)** — weighed after review and rejected: they shorten the forensic
   window of restart findings, the main value of the section, to defend a worst case that needs thousands of maximal commands; the
   bound stays fixed, only a signed-in client opens a circuit, and the incident page already holds about 10 MB per circuit while it
   reads an incident's window. The chosen caps keep the commands' page share below about 60 MB per circuit; bounding that share
   further is a follow-up.
3. **User names on the page as stored** — up to 1,024 bytes each; rejected for a cut to 32 UTF-8 bytes, the longest name `useradd`
   makes (64 bytes would add about 12.4 MB per circuit).
4. **Changed socket UIDs (#305) in the findings under the existing two caps** — no new cap, but a toggling shared endpoint would push
   findings with a new listener out (spec challenge). Rejected for a **third cap** for the agent findings that hold changes only; a
   finding with a new listener keeps its kind and lists its changes too.
5. **The changed endpoint's line with the owner's command** — like a new listener's, but the changes would add about 100 MB per
   circuit more. Rejected for the PID without the command (the command is in the stored record and the process snapshot); the stored
   changes leave the command out as well.

## Decision

The section *New listeners* caps the findings after an agent start, the agent's own findings and the agent's change-only findings
separately at the latest few of each kind, shows user names cut, and lists changed endpoints with the PID but without the command. The
rules are in [Listener changes](../areas/listener-changes.md), the page's memory in [Report page](../areas/web-ui-report-page.md).

## Consequences

- Findings are "latest" by capture time (then agent ID and `seq`) for the caps, so a clock that steps back may keep an older finding
  instead of a newer one; the order shown within an agent is by `seq`.
- A local user who toggles a shared endpoint can fill the change-only cap and push an earlier takeover out of it; the cap note shows
  the number left out and the rows stay stored.
- The page's worst case per circuit is large and stated in the Report page area; lowering it is a follow-up, not part of this record.
